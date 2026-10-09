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

        // Adapters capture before changing native state. This final bound is also enforced at the storage boundary.
        public static FailureIncident Bound(FailureIncident input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            // Do not serialize an unbounded input just to clone it; copy metadata and bound each message first.
            var incident = new FailureIncident { IncidentId = input.IncidentId, OperationId = input.OperationId, ModelScopeKey = input.ModelScopeKey,
                Source = input.Source, TransactionName = input.TransactionName, RequestHash = input.RequestHash, DocumentRevision = input.DocumentRevision,
                RevitYear = input.RevitYear, OperationContext = input.OperationContext, NativeAction = input.NativeAction, NativeContextHash = input.NativeContextHash,
                NativeContextDescription = input.NativeContextDescription, ModelTitle = input.ModelTitle, RelatedModelScopeKeys = input.RelatedModelScopeKeys.ToList(), BatchIndex = input.BatchIndex, Severity = input.Severity,
                Action = input.Action, State = input.State, Complete = input.Complete, CountKind = input.CountKind, MessageCount = input.MessageCount,
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
            + "If you can wait, call revit_get_operation_status again later until execution is completed; if you cannot, tell the user the operation is still waiting for them. "
            + "revit_lookup_message explains a dialog or failure code. Do not retry unchanged.";

        // Written for the agent: it cannot act in Revit UI, but it can read, resurvey and revise its plan.
        public static string NextStep(string errorCode, bool gateBlocked, bool acceptedWarnings)
        {
            const string records = "Read the incidents with revit_get_failure_records (includeDetails=true)";
            const string decide = "Only the user can rework, abandon or accept it in Revit (History > Issues).";
            const string reads = "Typed read tools (views, elements, parameters, survey, warnings, history) still work; "
                + "revit_send_code_to_revit and revit_batch_execute count as writes even when they only read, and other write tools are blocked.";
            switch (errorCode)
            {
                case "REVIT_FAILURE_ROLLED_BACK":
                    if (gateBlocked) break;
                    return "Revit rejected your change and rolled it back; this operation left the model unchanged. " + records
                        + ", survey the listed elements with read tools, and revise the plan. The model stays writable, but the same request "
                        + "is refused while the incident is open: do not resend it unchanged. The incident stays open for the user to rework, "
                        + "abandon or accept in Revit (History > Issues); mention it when you report to the user.";
                case "REVIT_REQUEST_REPEATED":
                    return "Not started: Revit already rolled back this exact request (incidentIds) and the user has not decided yet. "
                        + records + ", then change the request based on what you find, or ask the user to accept it in Revit (History > Issues). "
                        + "Other writes to this model still work.";
                case "REVIT_MODEL_BLOCKED":
                    return "Not started: this model has open incidents whose outcome is uncertain (incidentIds). " + records
                        + ". " + reads + " Tell the user what happened and what you propose. " + decide;
                case "REVIT_OPERATION_REQUIRES_USER":
                    return "This native action needs the user in Revit and was not completed by the agent. Check _operation for the actual outcome, "
                        + "tell the user what you intended, and let them perform or approve it in Revit. Do not retry unchanged.";
                case "FAILURE_CAPTURE_INCOMPLETE":
                case "FAILURE_STORAGE_FAILED":
                    return "The failure could not be fully recorded; treat the outcome as uncertain. Inspect the model with read tools "
                        + "and ask the user to review it in Revit before further writes. Do not retry unchanged.";
            }
            if (errorCode != null || gateBlocked)
                return "The outcome needs review and writes to this model are blocked. " + records + ", inspect the model with read tools, and tell the user. " + reads + " " + decide;
            if (acceptedWarnings)
                return "Committed with warnings the user accepted by rule or one-use grant (see the incident). The warnings remain in the model; this does not certify design quality.";
            return "Revit reported conditions during this operation. " + records + " before relying on the result.";
        }

        public static JObject Summary(FailureIncident incident, bool? mutationApplied, string gate)
        {
            var bounded = Bound(incident);
            var groups = new JArray(bounded.Messages.GroupBy(m => new { m.DefinitionId, m.Severity }).Take(20).Select(g => new JObject {
                ["failureDefinitionId"] = g.Key.DefinitionId, ["severity"] = g.Key.Severity, ["count"] = g.Count(),
                ["description"] = SafeText(g.First().Description, 256),
                ["sampleElementIds"] = new JArray(g.SelectMany(m => m.FailingElementIds).Distinct().Take(20)),
                ["resolutionsApplied"] = g.Any(m => m.ResolutionsApplied) }));
            var result = new JObject { ["schemaVersion"] = 1, ["operationId"] = bounded.OperationId,
                ["incidentIds"] = new JArray(bounded.IncidentId), ["modelScopeKey"] = bounded.ModelScopeKey,
                ["action"] = bounded.Action, ["mutationApplied"] = mutationApplied.HasValue ? (JToken)mutationApplied.Value : JValue.CreateNull(),
                ["gate"] = gate, ["persistence"] = bounded.Persistence, ["complete"] = bounded.Complete,
                ["messageCount"] = bounded.MessageCount, ["countKind"] = bounded.CountKind, ["groups"] = groups,
                ["nextStep"] = NextStep("REVIT_FAILURE_ROLLED_BACK", gate == "blocked", false) };
            while (Encoding.UTF8.GetByteCount(result.ToString(Newtonsoft.Json.Formatting.None)) > 16 * 1024 && groups.Count > 0)
                groups.RemoveAt(groups.Count - 1);
            return result;
        }
    }
}
