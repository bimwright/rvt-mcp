using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RvtMcp.Failures;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Presentation.Issues
{
    internal static class IssuesText
    {
        internal static string DetailsText(FailureIncident incident, FailureOperation operation, string modelTitle)
        {
            if (incident == null) return "";
            var text = new StringBuilder();
            text.AppendLine(incident.ModelTitle ?? modelTitle);
            text.AppendLine(L.T("failure.detail.identity", ("incident", incident.IncidentId),
                ("operation", incident.OperationId), ("scope", incident.ModelScopeKey)));
            text.AppendLine((operation?.Tool ?? Unknown()) + " · " + (operation?.Execution ?? Unknown())
                + " · " + (operation?.Outcome ?? Unknown()) + " · " + incident.State);
            text.AppendLine(L.T("failure.detail.context", ("context", incident.OperationContext ?? Unknown()),
                ("year", incident.RevitYear), ("transaction", incident.TransactionName ?? Unknown()),
                ("coverage", operation?.ObservationCoverage ?? Unknown()),
                ("complete", incident.Complete && operation?.Complete == true), ("persistence", incident.Persistence)));
            if (FailureIssuesPresentation.NeedsModelVerification(operation)) text.AppendLine(L.T("failure.detail.partial"));
            if (!incident.Complete || operation?.Complete != true) text.AppendLine(L.T("failure.detail.incomplete"));
            text.AppendLine(L.T("failure.detail.temporary"));
            text.AppendLine("Request: " + incident.RequestHash + "\nRevision: " + incident.DocumentRevision);
            text.AppendLine(L.T("security.warningPolicy.label") + ": " + incident.WarningPolicy);
            if (!string.IsNullOrEmpty(incident.NativeAction))
            {
                text.AppendLine(incident.NativeAction);
                text.AppendLine(incident.NativeContextDescription);
                text.AppendLine("Context fingerprint: " + incident.NativeContextHash);
            }
            var messages = incident.Messages ?? new List<FailureMessage>();
            foreach (var message in messages)
            {
                text.AppendLine();
                text.AppendLine(message.Severity + " · " + message.DefinitionId);
                text.AppendLine(message.Description);
                var ids = message.FailingElementIds.Concat(message.AdditionalElementIds).Distinct().ToList();
                text.AppendLine(L.T("failure.detail.elements", ("ids", string.Join(", ", ids))));
                if (!message.Complete || message.TextTruncated) text.AppendLine(L.T("failure.detail.incomplete"));
                foreach (var snapshot in message.Snapshots ?? new List<FailureElementSnapshot>())
                    text.AppendLine(snapshot.ElementId + " · " + snapshot.Category + " · " + snapshot.Type
                        + " · " + snapshot.Level + " · existsAfterRollback=" + (snapshot.ExistsAfterRollback?.ToString() ?? Unknown()));
            }
            if (FailureIssuesPresentation.CanAcceptWarning(incident))
            {
                text.AppendLine();
                foreach (var rule in FailureIssuesPresentation.WarningConstraints(incident)) text.AppendLine(RuleText(rule));
            }
            return text.ToString();
        }

        internal static string RuleText(FailureRule rule) => (rule.Severity == "TaskDialog" ? ProtectedRuleText(rule, null, null) : L.T("failure.detail.rule",
            ("definition", rule.DefinitionId), ("severity", rule.Severity), ("context", rule.OperationContext),
            ("year", rule.RevitYear), ("count", rule.MaxCount), ("elements", string.Join(", ", rule.ExistingElementIds))))
            + " · " + L.T(rule.Enabled ? "failure.rule.enabled" : "failure.rule.revoked") + " · v" + rule.Revision;

        internal static string ProtectedConstraints(FailureIncident incident, FailureDecision decision, FailureIssuesContext context)
        {
            // Decision facts must use locked security keys; ordinary user-overridable labels cannot hide a constraint.
            if (decision.Action == "native_grant")
                return L.T("security.failure.nativeConstraints", ("scope", decision.ModelScopeKey),
                    ("action", decision.Grant.NativeAction), ("details", incident.NativeContextDescription),
                    ("year", decision.Grant.RevitYear), ("context", decision.Grant.OperationContext),
                    ("fingerprint", decision.Grant.NativeContextHash), ("request", decision.Grant.RequestHash),
                    ("revision", decision.Grant.DocumentRevision));
            var rules = decision.Rules.Cast<FailureRule>().Concat(decision.Grants).ToList();
            return string.Join("\n\n", rules.Select(rule => ProtectedRuleText(rule, incident.RequestHash, decision.DocumentRevision)));
        }

        internal static string ProtectedRuleText(FailureRule rule, string request, string revision)
        {
            var adapter = rule.Severity == "TaskDialog" ? FailureDialogAdapters.Get(rule.DefinitionId, rule.RevitYear, rule.OperationContext) : null;
            if (adapter != null)
                return L.T("security.failure.dialogConstraints", ("scope", rule.ModelScopeKey), ("dialog", adapter.DialogId),
                    ("year", rule.RevitYear), ("context", adapter.OperationContext), ("semantic", DialogSemantic(adapter)),
                    ("result", adapter.Result), ("request", request ?? "—"), ("revision", revision ?? "—"));
            return L.T("security.failure.constraints", ("scope", rule.ModelScopeKey), ("definition", rule.DefinitionId),
                ("year", rule.RevitYear), ("context", rule.OperationContext), ("count", rule.MaxCount),
                ("elements", string.Join(", ", rule.ExistingElementIds)), ("request", request ?? "—"), ("revision", revision ?? "—"));
        }

        internal static string DialogSemantic(FailureDialogAdapter adapter) => L.T(adapter.DialogId == "TaskDialog_Unresolved_References"
            ? "security.failure.dialogUnresolved" : "security.failure.dialogAnalytical");

        internal static string ActionLabel(string action) => L.T("security.failure." +
            (action == "accept_once" ? "acceptOnce" : action == "create_rule" ? "createRule"
                : action == "native_grant" ? "nativeGrant" : action));

        private static string Unknown() => L.T("failure.detail.unknown");
    }
}
