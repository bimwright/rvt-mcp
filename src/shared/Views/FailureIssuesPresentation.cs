using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using RvtMcp.Failures;

namespace RvtMcp.Plugin.Views
{
    /// <summary>Pure proposal construction. The native facade and ledger validate again when saving.</summary>
    internal static class FailureIssuesPresentation
    {
        /// <summary>Actions that permit a later attempt open a review of their exact constraints. Rework and Abandon only close the issue.</summary>
        internal static bool RequiresConfirmation(string action)
        {
            return action == "accept_once" || action == "create_rule" || action == "native_grant";
        }

        internal static bool IssueModelIsOpen(string modelScopeKey, ICollection<string> openScopes)
        {
            return !string.IsNullOrEmpty(modelScopeKey) && openScopes != null && openScopes.Contains(modelScopeKey);
        }

        // Approximate substring matching: one edit per five query characters is the 80% cutoff.
        // Adjacent transpositions count as one edit. Matching only titles keeps scope IDs out of search results.
        internal static double ModelNameSearchScore(string title, string query)
        {
            var pattern = NormalizeModelSearch(query);
            if (pattern.Length == 0) return 1;
            var text = NormalizeModelSearch(title);
            if (text.Length == 0) return 0;
            if (text.Contains(pattern)) return 1;
            var previous = new int[text.Length + 1]; // Zero-cost prefixes allow a match anywhere in the title.
            var beforePrevious = new int[text.Length + 1];
            var current = new int[text.Length + 1];
            for (var i = 1; i <= pattern.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= text.Length; j++)
                {
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + (pattern[i - 1] == text[j - 1] ? 0 : 1));
                    if (i > 1 && j > 1 && pattern[i - 1] == text[j - 2] && pattern[i - 2] == text[j - 1])
                        current[j] = Math.Min(current[j], beforePrevious[j - 2] + 1);
                }
                var reuse = beforePrevious; beforePrevious = previous; previous = current; current = reuse;
            }
            return 1.0 - (double)previous.Min() / pattern.Length;
        }

        private static string NormalizeModelSearch(string value)
        {
            var text = new StringBuilder();
            foreach (var ch in (value ?? "").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                var lower = char.ToLowerInvariant(ch);
                if (char.IsLetterOrDigit(lower)) text.Append(lower == 'đ' ? 'd' : lower);
            }
            return text.ToString();
        }

        /// <summary>Prefer the active project's row for this Revit year, then any row of that project, then the first row.</summary>
        internal static FailureIssueModel PreferredIssueModel(IReadOnlyList<FailureIssueModel> models, string activeScope, int activeYear)
        {
            if (models == null || models.Count == 0) return null;
            FailureIssueModel sameProject = null;
            foreach (var model in models)
            {
                if (!string.Equals(model?.ModelScopeKey, activeScope, StringComparison.Ordinal)) continue;
                if (model.RevitYear == activeYear) return model;
                if (sameProject == null) sameProject = model;
            }
            return sameProject ?? models[0];
        }

        internal static bool NeedsModelVerification(FailureOperation operation)
        {
            return operation == null || operation.Outcome == "partial" || operation.Outcome == "unknown"
                || operation.Execution == "interrupted_unknown";
        }

        internal static bool CanAcceptWarning(FailureIncident incident)
        {
            return incident != null && incident.State == "open" && incident.Complete
                && incident.CountKind == "exact" && incident.OmittedMessageCount == 0
                && incident.OmittedElementCount == 0 && incident.Messages != null
                && incident.Messages.Count > 0 && incident.MessageCount == incident.Messages.Count
                && incident.RevitYear >= 2022 && !string.IsNullOrWhiteSpace(incident.OperationContext)
                && incident.Messages.All(m => m.Complete && !m.TextTruncated && !m.ResolutionsApplied
                    && m.Severity == "Warning" && Guid.TryParse(m.DefinitionId, out _));
        }

        internal static List<FailureRule> WarningConstraints(FailureIncident incident)
        {
            if (!CanAcceptWarning(incident)) throw new InvalidOperationException("security.failure.warningOnly");
            return incident.Messages.GroupBy(m => m.DefinitionId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FailureRule {
                    ModelScopeKey = incident.ModelScopeKey,
                    DefinitionId = group.Key,
                    Severity = "Warning",
                    RevitYear = incident.RevitYear,
                    OperationContext = incident.OperationContext,
                    MaxCount = group.Count(),
                    ExistingElementIds = group.SelectMany(m => m.ExistingElementIds ?? new List<long>())
                        .Distinct().OrderBy(id => id).ToList(),
                    Enabled = true
                }).ToList();
        }

        internal static FailureDialogAdapter DialogAdapter(FailureIncident incident)
        {
            if (incident == null || (incident.State != "open" && incident.State != "observed") || !incident.Complete
                || incident.CountKind != "exact" || incident.OmittedMessageCount != 0 || incident.OmittedElementCount != 0
                || incident.MessageCount != 1 || incident.Messages?.Count != 1) return null;
            var message = incident.Messages[0];
            if (message.Severity != "TaskDialog" || !message.Complete || message.TextTruncated || message.ResolutionsApplied
                || message.FailingElementIds.Count != 0 || message.AdditionalElementIds.Count != 0
                || message.ExistingElementIds.Count != 0) return null;
            return FailureDialogAdapters.Get(message.DefinitionId, incident.RevitYear, incident.OperationContext);
        }

        internal static List<FailureRule> AcceptanceConstraints(FailureIncident incident)
        {
            var adapter = DialogAdapter(incident);
            if (adapter == null) return WarningConstraints(incident);
            return new List<FailureRule> { new FailureRule {
                ModelScopeKey = incident.ModelScopeKey, DefinitionId = adapter.DialogId, Severity = "TaskDialog",
                RevitYear = incident.RevitYear, OperationContext = adapter.OperationContext,
                MaxCount = 1, Enabled = true
            } };
        }

        internal static FailureDecision CreateDecision(string action, FailureIncident incident,
            FailureOperation operation, FailureIssuesContext context, string reason, bool verifiedModel)
        {
            if (incident == null) throw new InvalidOperationException("security.failure.stale");
            // Review belongs to the selected incident, never to whichever project happens to be active.
            var revision = context?.IsAvailable == true && context.ModelScopeKey == incident.ModelScopeKey
                && context.RevitYear == incident.RevitYear ? context.DocumentRevision : incident.DocumentRevision;
            if (incident.State != "open" && !(incident.State == "observed" && action == "create_rule" && DialogAdapter(incident) != null))
                throw new InvalidOperationException("security.failure.stale");
            if (NeedsModelVerification(operation) && !verifiedModel)
                throw new InvalidOperationException("security.failure.verificationRequired");
            if (action != "rework" && action != "abandon" && action != "accept_once" && action != "create_rule")
                throw new ArgumentOutOfRangeException(nameof(action));

            var decision = new FailureDecision {
                IncidentId = incident.IncidentId, ModelScopeKey = incident.ModelScopeKey,
                Action = action, Reason = FailureDecision.NormalizeReason(reason), WindowsUser = Environment.UserName,
                RevitUser = context?.RevitUser, HostInstanceId = context?.HostInstanceId,
                DocumentRevision = revision,
                Channel = "revit_ui", VerifiedModel = verifiedModel,
                Verification = verifiedModel ? "human_asserted" : null
            };
            if (action == "rework" || action == "abandon") return decision;
            var rules = AcceptanceConstraints(incident);
            if (action == "create_rule")
            {
                decision.Rules = rules;
                return decision;
            }
            if (string.IsNullOrWhiteSpace(incident.RequestHash) || string.IsNullOrWhiteSpace(revision))
                throw new InvalidOperationException("security.failure.stale");
            if (revision != incident.DocumentRevision && !verifiedModel)
                throw new InvalidOperationException("security.failure.revisionVerificationRequired");
            decision.Grants = rules.Select(rule => new FailureGrant {
                ModelScopeKey = rule.ModelScopeKey, DefinitionId = rule.DefinitionId,
                Severity = rule.Severity, RevitYear = rule.RevitYear,
                OperationContext = rule.OperationContext, MaxCount = rule.MaxCount,
                ExistingElementIds = rule.ExistingElementIds,
                RequestHash = incident.RequestHash, DocumentRevision = revision,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5), Enabled = true
            }).ToList();
            return decision;
        }

        internal static FailureDecision CreateNativeDecision(FailureIncident incident, FailureOperation operation,
            FailureIssuesContext context, string reason, bool verifiedModel)
        {
            var decision = CreateDecision("rework", incident, operation, context, reason, verifiedModel);
            if (!incident.Complete || string.IsNullOrWhiteSpace(incident.NativeAction) || string.IsNullOrWhiteSpace(incident.NativeContextHash)
                || string.IsNullOrWhiteSpace(incident.NativeContextDescription) || string.IsNullOrWhiteSpace(incident.RequestHash)
                || string.IsNullOrWhiteSpace(decision.DocumentRevision))
                throw new InvalidOperationException("security.failure.stale");
            if (decision.DocumentRevision != incident.DocumentRevision && !verifiedModel)
                throw new InvalidOperationException("security.failure.revisionVerificationRequired");
            decision.Action = "native_grant";
            decision.Grant = new FailureGrant {
                ModelScopeKey = incident.ModelScopeKey, RevitYear = incident.RevitYear,
                OperationContext = incident.OperationContext, NativeAction = incident.NativeAction,
                NativeContextHash = incident.NativeContextHash, RequestHash = incident.RequestHash,
                DocumentRevision = decision.DocumentRevision, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            return decision;
        }
    }
}
