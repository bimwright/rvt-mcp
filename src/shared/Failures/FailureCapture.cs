using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Failures
{
    public static class FailureCapture
    {
        public const int MaxMessages = 5000, MaxElementRefs = 50000, MaxSnapshots = 200, MaxText = 4096;
        public const int MaxOperationBytes = 32 * 1024 * 1024;
        private static readonly Regex Paths = new Regex(@"(?i)(?:[a-z]:[\\/]|\\\\)[^\s\r\n\""<>|]*", RegexOptions.Compiled);
        public static string SafeText(string value, int max = MaxText)
        {
            var text = RvtMcp.Plugin.SecretMasker.Mask(value ?? "");
            text = Paths.Replace(text, "[local path]");
            return text.Length <= max ? text : text.Substring(0, Math.Max(0, max - 12)) + " [truncated]";
        }
        public static string Fingerprint(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }
        public static bool IsKey(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= 128
                && value.All(c => char.IsLetterOrDigit(c) && c <= 127 || c == '_' || c == '-');
        }
        public static void RequireKey(string value, string name)
        { if (!IsKey(value)) throw new ArgumentException("Invalid " + name + ".", name); }

        public static string ReceiptModelScopeKey(string failureModelKey, string tool, string activeModelBefore,
            IEnumerable<string> modelScopeKeys, IEnumerable<string> incidentModelScopeKeys)
        {
            if (!string.IsNullOrWhiteSpace(failureModelKey)) return failureModelKey;
            var incidentKeys = (incidentModelScopeKeys ?? Enumerable.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (incidentKeys.Length == 1) return incidentKeys[0];
            if (incidentKeys.Length > 1) return null;
            // The active document before typed open_model is context, never its mutation target.
            if (tool == "open_model")
            {
                var targets = (modelScopeKeys ?? Enumerable.Empty<string>())
                    .Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                return targets.Length == 1 ? targets[0] : null;
            }
            return activeModelBefore;
        }

        // Adapters capture before changing native state. This final bound is also enforced at the storage boundary.
        public static FailureIncident Bound(FailureIncident input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            // Do not serialize an unbounded input just to clone it; copy metadata and bound each message first.
            var incident = new FailureIncident { IncidentId = input.IncidentId, OperationId = input.OperationId, ModelScopeKey = input.ModelScopeKey,
                Source = input.Source, TransactionName = input.TransactionName, RequestHash = input.RequestHash, DocumentRevision = input.DocumentRevision,
                RevitYear = input.RevitYear, OperationContext = input.OperationContext, NativeAction = input.NativeAction, NativeContextHash = input.NativeContextHash,
                NativeContextDescription = input.NativeContextDescription, ModelTitle = input.ModelTitle, RelatedModelScopeKeys = input.RelatedModelScopeKeys.ToList(), BatchIndex = input.BatchIndex, Severity = input.Severity,
                Action = input.Action, State = input.State, WarningPolicy = WarningPolicyModes.Normalize(input.WarningPolicy),
                Complete = input.Complete, CountKind = input.CountKind, MessageCount = input.MessageCount,
                OmittedMessageCount = input.OmittedMessageCount, OmittedElementCount = input.OmittedElementCount, RuleIds = input.RuleIds.ToList(),
                GrantIds = input.GrantIds.ToList(), RuleRevisions = new Dictionary<string, long>(input.RuleRevisions), Persistence = input.Persistence, CreatedUtc = input.CreatedUtc };
            incident.TransactionName = SafeText(incident.TransactionName, 256);
            incident.ModelTitle = SafeText(incident.ModelTitle, 256);
            var captured = new List<FailureMessage>(); var ids = new HashSet<long>(); var snapshots = new HashSet<long>();
            var evidence = new Dictionary<string, string>(StringComparer.Ordinal); long bytes = 0;
            int deduplicated = 0;
            foreach (var original in input.Messages)
            {
                if (original == null) { incident.Complete = false; incident.OmittedMessageCount++; continue; }
                if (captured.Count >= MaxMessages) { incident.OmittedMessageCount++; incident.Complete = false; continue; }
                var message = new FailureMessage { DefinitionId = original.DefinitionId, Description = SafeText(original.Description),
                    Severity = original.Severity, Source = original.Source, TransactionKey = original.TransactionKey, Cycle = original.Cycle,
                    Sequence = original.Sequence, Complete = original.Complete, TextTruncated = original.TextTruncated || (original.Description?.Length ?? 0) > MaxText,
                    ResolutionsApplied = original.ResolutionsApplied };
                // Text alone is not identity. Dedup only exact cross-adapter observations with native cycle evidence.
                string key = null;
                if (!string.IsNullOrEmpty(message.TransactionKey) && !string.IsNullOrEmpty(message.Cycle) && message.Sequence.HasValue
                    && message.Complete && !message.TextTruncated && original.FailingElementIds.Count <= MaxElementRefs && original.AdditionalElementIds.Count <= MaxElementRefs)
                    key = message.TransactionKey + ":" + message.Cycle + ":" + message.Sequence + ":" + Fingerprint(
                        message.DefinitionId + "|" + message.Severity + "|" + original.Description + "|"
                        + string.Join(",", original.FailingElementIds.Take(MaxElementRefs).OrderBy(i => i)) + "|" + string.Join(",", original.AdditionalElementIds.Take(MaxElementRefs).OrderBy(i => i)));
                string previous;
                if (key != null && evidence.TryGetValue(key, out previous) && previous != message.Source
                    && !string.IsNullOrEmpty(previous) && !string.IsNullOrEmpty(message.Source)) { deduplicated++; continue; }
                if (key != null) evidence[key] = message.Source;
                if (message.TextTruncated) { message.Complete = false; incident.Complete = false; }
                message.DefinitionId = SafeText(message.DefinitionId, 128);
                var originals = new[] { original.FailingElementIds, original.AdditionalElementIds, original.ExistingElementIds };
                var lists = new[] { message.FailingElementIds, message.AdditionalElementIds, message.ExistingElementIds };
                for (var listIndex = 0; listIndex < lists.Length; listIndex++)
                {
                    var keep = lists[listIndex];
                    foreach (var id in originals[listIndex].Distinct())
                    {
                        if (ids.Contains(id) || ids.Count < MaxElementRefs) { ids.Add(id); keep.Add(id); }
                        else { incident.OmittedElementCount++; message.Complete = false; incident.Complete = false; }
                    }
                }
                message.Snapshots = original.Snapshots.Where(s => s != null && ids.Contains(s.ElementId)
                    && (snapshots.Contains(s.ElementId) || snapshots.Count < MaxSnapshots) && snapshots.Add(s.ElementId))
                    .Select(s => new FailureElementSnapshot { ElementId = s.ElementId, Category = s.Category, Type = s.Type, Level = s.Level,
                        Position = s.Position == null ? null : s.Position.Take(3).ToArray(), ExistsAfterRollback = s.ExistsAfterRollback }).ToList();
                foreach (var snapshot in message.Snapshots)
                {
                    snapshot.Category = SafeText(snapshot.Category, 256); snapshot.Type = SafeText(snapshot.Type, 256);
                    snapshot.Level = SafeText(snapshot.Level, 256);
                    if (snapshot.Position != null && snapshot.Position.Length != 3) snapshot.Position = null;
                }
                bytes += Encoding.UTF8.GetByteCount(FailureJson.Serialize(message));
                if (bytes > MaxOperationBytes - 65536) { incident.OmittedMessageCount++; incident.Complete = false; continue; }
                incident.Complete &= message.Complete;
                captured.Add(message);
            }
            incident.Messages = captured;
            incident.MessageCount = Math.Max(incident.MessageCount - deduplicated, captured.Count + incident.OmittedMessageCount);
            if (!incident.Complete) incident.CountKind = "at_least";
            return incident;
        }

        // Mechanism only: how to react to the dialog is the agent's judgement.
        public const string WaitingNextStep = "Revit is showing a dialog and waits for the user. Other model calls are refused until it closes. "
            + "Call revit_get_operation_status with waitSeconds (up to 90) to wait for the result; if it is still waiting, tell the user the operation is waiting for them in Revit. "
            + "revit_lookup_message explains a dialog or failure code. Do not retry unchanged.";

        // A timeout is not a dialog: nothing reported one, and the request may simply still be running.
        public const string TimedOutNextStep = "Revit has not finished this request within the timeout and reported no dialog; it may still be running. "
            + "Call revit_get_operation_status with this operationId and waitSeconds (up to 90) to wait for the result. Do not retry unchanged.";

        public const string NotStartedNextStep = "Not started: Revit was busy (another request was running, or Revit was not idle), so this request never ran and nothing changed. "
            + "Send it again once Revit is free.";

        // Written for the agent: it cannot act in Revit UI, but it can read, resurvey and revise its plan.
        public static string NextStep(string errorCode, bool reviewRequired, bool acceptedWarnings, string warningPolicy = WarningPolicyModes.Rollback)
        {
            const string records = "Read the incidents with revit_get_failure_records (includeDetails=true)";
            const string review = "The user can review it in Revit (History > Issues).";
            const string admission = "Previous Issues do not block subsequent MCP commands or repeated requests. ";
            switch (errorCode)
            {
                case "REVIT_FAILURE_ROLLED_BACK":
                    if (reviewRequired) return "Revit rejected this operation, but the full outcome is uncertain or earlier changes may remain. "
                        + records + ", inspect the model and revise the plan. " + admission + review;
                    return "Revit rejected your change and rolled it back; this operation left the model unchanged. " + records
                        + ", survey the listed elements with read tools, and revise the plan. The model stays writable. " + admission + review;
                case "REVIT_REQUEST_REPEATED":
                case "REVIT_MODEL_BLOCKED":
                    return "An earlier build refused this request because of recorded issues. " + records
                        + ", inspect the actual outcome and revise the plan as needed. " + admission + review;
                case "REVIT_BUSY":
                    return NotStartedNextStep;
                case "REVIT_OPERATION_REQUIRES_USER":
                    return "This native action needs the user in Revit and was not completed by the agent. Check _operation for the actual outcome, "
                        + "tell the user what you intended, and let them perform or approve it in Revit. Do not retry unchanged.";
                case "FAILURE_CAPTURE_INCOMPLETE":
                case "FAILURE_STORAGE_FAILED":
                    return "The failure could not be fully recorded; treat the outcome as uncertain. Inspect the model with read tools "
                        + "and report the recording failure to the user. " + admission + review;
            }
            if (errorCode != null || reviewRequired)
                return "The outcome needs review. " + records + ", inspect the model with read tools, and tell the user. " + admission + review;
            if (acceptedWarnings && warningPolicy == WarningPolicyModes.AcceptAndLog)
                return "Warnings were accepted and recorded under the user's accept-and-log policy. Check the recorded outcome; acceptance does not certify model correctness or repair earlier operations.";
            if (acceptedWarnings)
                return "Committed with warnings the user accepted by rule or one-use grant (see the incident). The warnings remain in the model; this does not certify design quality.";
            return "Revit reported conditions during this operation. " + records + " before relying on the result.";
        }

        // Who closed a dialog group: a rule answer is kept per occurrence, the rest of the count was closed by the user.
        // One answer stands for the group only when a rule gave it to every occurrence.
        public static void DescribeAnswers(JObject group, IReadOnlyList<JObject> ruleAnswers)
        {
            int byRule = ruleAnswers?.Count ?? 0, byUser = Math.Max(0, (group.Value<int?>("count") ?? 0) - byRule);
            group["answeredBy"] = byRule == 0 ? "user" : byUser == 0 ? "rule" : "mixed";
            if (byRule > 0 && byUser > 0) group["answeredCount"] = new JObject { ["rule"] = byRule, ["user"] = byUser };
            if (byRule > 0 && byUser == 0 && ruleAnswers.Distinct(JToken.EqualityComparer).Count() == 1) group["answer"] = ruleAnswers[0].DeepClone();
        }

        public static JObject Summary(FailureIncident incident, bool? mutationApplied, string gate)
        {
            var bounded = Bound(incident);
            bool reviewRequired = gate == "blocked" || !bounded.Complete;
            bool acceptedWarnings = mutationApplied == true && bounded.Action == "accepted_warning"
                && (bounded.State == "accepted_by_policy" || bounded.State == "accepted_by_rule" || bounded.State == "accepted_once");
            var groups = new JArray(bounded.Messages.GroupBy(m => new { m.DefinitionId, m.Severity }).Take(20).Select(g => new JObject {
                ["failureDefinitionId"] = g.Key.DefinitionId, ["severity"] = g.Key.Severity, ["count"] = g.Count(),
                ["description"] = SafeText(g.First().Description, 256),
                ["sampleElementIds"] = new JArray(g.SelectMany(m => m.FailingElementIds).Distinct().Take(20)),
                ["resolutionsApplied"] = g.Any(m => m.ResolutionsApplied) }));
            var result = new JObject { ["schemaVersion"] = 1, ["operationId"] = bounded.OperationId,
                ["incidentIds"] = new JArray(bounded.IncidentId), ["modelScopeKey"] = bounded.ModelScopeKey,
                ["action"] = bounded.Action, ["mutationApplied"] = mutationApplied.HasValue ? (JToken)mutationApplied.Value : JValue.CreateNull(),
                ["gate"] = "open", ["requiresReview"] = reviewRequired, ["warningPolicy"] = bounded.WarningPolicy,
                ["persistence"] = bounded.Persistence, ["complete"] = bounded.Complete,
                ["messageCount"] = bounded.MessageCount, ["countKind"] = bounded.CountKind, ["groups"] = groups,
                ["nextStep"] = NextStep(acceptedWarnings ? null : "REVIT_FAILURE_ROLLED_BACK", reviewRequired, acceptedWarnings,
                    bounded.State == "accepted_by_policy" ? bounded.WarningPolicy : WarningPolicyModes.Rollback) };
            while (Encoding.UTF8.GetByteCount(result.ToString(Newtonsoft.Json.Formatting.None)) > 16 * 1024 && groups.Count > 0)
                groups.RemoveAt(groups.Count - 1);
            return result;
        }
    }
}
