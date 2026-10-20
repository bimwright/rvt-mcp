using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Failures;

namespace RvtMcp.Plugin.Views
{
    /// <summary>Pure proposal construction. The native facade and ledger validate again when saving.</summary>
    internal static class FailureIssuesPresentation
    {
        internal static bool IssueModelIsOpen(string modelScopeKey, ICollection<string> openScopes)
        {
            return !string.IsNullOrEmpty(modelScopeKey) && openScopes != null && openScopes.Contains(modelScopeKey);
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
            if (context == null || !context.IsAvailable || incident == null
                || context.ModelScopeKey != incident.ModelScopeKey)
                throw new InvalidOperationException("security.failure.currentModelRequired");
            if (incident.State != "open" && !(incident.State == "observed" && action == "create_rule" && DialogAdapter(incident) != null))
                throw new InvalidOperationException("security.failure.stale");
            if (NeedsModelVerification(operation) && !verifiedModel)
                throw new InvalidOperationException("security.failure.verificationRequired");
            if (action != "rework" && action != "abandon" && action != "accept_once" && action != "create_rule")
                throw new ArgumentOutOfRangeException(nameof(action));

            var decision = new FailureDecision {
                IncidentId = incident.IncidentId, ModelScopeKey = incident.ModelScopeKey,
                Action = action, Reason = FailureDecision.NormalizeReason(reason), WindowsUser = Environment.UserName,
                RevitUser = context.RevitUser, HostInstanceId = context.HostInstanceId,
                DocumentRevision = context.DocumentRevision,
                Channel = "revit_ui", VerifiedModel = verifiedModel,
                Verification = verifiedModel ? "human_asserted" : null
            };
            if (action == "rework" || action == "abandon") return decision;
            if (context.RevitYear != incident.RevitYear)
                throw new InvalidOperationException("security.failure.stale");
            var rules = AcceptanceConstraints(incident);
            if (action == "create_rule")
            {
                decision.Rules = rules;
                return decision;
            }
            if (string.IsNullOrWhiteSpace(incident.RequestHash) || string.IsNullOrWhiteSpace(context.DocumentRevision))
                throw new InvalidOperationException("security.failure.stale");
            if (context.DocumentRevision != incident.DocumentRevision && !verifiedModel)
                throw new InvalidOperationException("security.failure.revisionVerificationRequired");
            decision.Grants = rules.Select(rule => new FailureGrant {
                ModelScopeKey = rule.ModelScopeKey, DefinitionId = rule.DefinitionId,
                Severity = rule.Severity, RevitYear = rule.RevitYear,
                OperationContext = rule.OperationContext, MaxCount = rule.MaxCount,
                ExistingElementIds = rule.ExistingElementIds,
                RequestHash = incident.RequestHash, DocumentRevision = context.DocumentRevision,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5), Enabled = true
            }).ToList();
            return decision;
        }
    }
}
