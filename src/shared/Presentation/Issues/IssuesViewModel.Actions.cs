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
    internal sealed partial class IssuesViewModel
    {
        private void ResetLedger()
        {
            try
            {
                if (!_runtime.CanReset) { SetBanner(L.T("failure.reset.busy"), UiTone.Error); return; }
                if (!_dialogs.ConfirmReset(_runtime.ResetSummary(), _runtime.BackupFolder)) return;
                var backup = _runtime.Reset();
                Refresh();
                SetBanner(L.T("failure.reset.done", ("path", backup)));
            }
            catch (InvalidOperationException ex) when (ex.Message == "FAILURE_RESET_BUSY") { SetBanner(L.T("failure.reset.busy"), UiTone.Error); }
            catch (Exception ex) { SetBanner(L.T("failure.reset.failed", ("error", ex.Message)), UiTone.Error); }
        }
        private bool Confirm(string action, string warning, string heading, string constraints, string details, string reason) =>
            _dialogs.ConfirmDecision(new IssueDecisionReview { Action = action, Warning = warning, ConstraintsHeading = heading,
                Constraints = constraints, Details = details, Reason = reason });
        private string DetailsText() => IssuesText.DetailsText(_incident, _operation, SelectedModel?.Title);
        private void CopyDetails()
        {
            if (_incident == null) return;
            try { _dialogs.Copy(DetailsText() + "\n\n" + FailureJson.Serialize(_incident)); SetBanner(L.T("failure.copied")); }
            catch (Exception ex) { ShowFailure(ex.Message); }
        }
        private static string Unknown() => L.T("failure.detail.unknown");
        private void ShowDecisionFailure(Exception error) => SetBanner(error.Message.StartsWith("security.failure.", StringComparison.Ordinal)
            ? L.T(error.Message) : L.T("security.failure.saveFailed", ("error", error.Message)), UiTone.Error);
        private void ShowFailure(string error) => SetBanner(L.T("security.failure.saveFailed", ("error", error)), UiTone.Error);
        private void RecordDecision(string action)
        {
            if (_incident == null) return;
            try
            {
                var context = _runtime.Context(_incident.ModelScopeKey);
                var dialog = FailureIssuesPresentation.DialogAdapter(_incident);
                if (dialog != null && (action == "accept_once" || action == "create_rule") && !DialogConsent)
                    throw new InvalidOperationException("security.failure.dialogChoiceRequired");
                var decision = action == "native_grant"
                    ? FailureIssuesPresentation.CreateNativeDecision(_incident, _operation, context, Reason, VerifiedModel)
                    : FailureIssuesPresentation.CreateDecision(action, _incident, _operation, context,
                        Reason, VerifiedModel);
                var warning = action == "accept_once" ? L.T(dialog != null ? "security.failure.dialogOnceWarning" : "security.failure.onceWarning")
                    : action == "create_rule" ? L.T(dialog != null ? "security.failure.dialogRuleWarning" : "security.failure.ruleWarning")
                    : action == "native_grant" ? L.T("security.failure.nativeWarning") : "";
                var label = dialog != null && action == "accept_once" ? L.T("security.failure.acceptDialogOnce") : IssuesText.ActionLabel(action);
                if (FailureIssuesPresentation.RequiresConfirmation(action)
                    && !Confirm(label, warning, L.T("security.failure.review.permits"), IssuesText.ProtectedConstraints(_incident, decision, context), DetailsText(), decision.Reason)) return;
                SaveDecision(decision);
            }
            catch (Exception ex) { ShowDecisionFailure(ex); }
        }

        private void RevokeRule()
        {
            var rule = SelectedRule?.Rule;
            if (rule == null || !rule.Enabled) return;
            try
            {
                var context = _runtime.Context();
                if (context?.IsAvailable != true || context.ModelScopeKey != rule.ModelScopeKey)
                    throw new InvalidOperationException("security.failure.currentModelRequired");
                var reason = FailureDecision.NormalizeReason(Reason);
                if (!Confirm(L.T("security.failure.revokeRule"), "", L.T("failure.rules"), IssuesText.ProtectedRuleText(rule, null, context.DocumentRevision), "", reason)) return;
                SaveDecision(new FailureDecision {
                    ModelScopeKey = rule.ModelScopeKey, Action = "revoke_rule", Rule = rule, Reason = reason,
                    WindowsUser = _runtime.WindowsUser, RevitUser = context.RevitUser, HostInstanceId = context.HostInstanceId,
                    Channel = "revit_ui", DocumentRevision = context.DocumentRevision
                });
            }
            catch (Exception ex) { ShowDecisionFailure(ex); }
        }

        private void SaveDecision(FailureDecision decision)
        {
            var result = _runtime.Save(decision);
            if (result == null || !result.Recorded || !result.Durable)
            {
                ShowFailure(result?.ErrorCode ?? "FAILURE_STORAGE_FAILED");
                return;
            }
            Reason = ""; VerifiedModel = false; DialogConsent = false;
            Refresh();
            SetBanner(L.T("failure.decision.saved") + " · " + PageStatus, _bannerTone);
        }

        internal void CopyWarning(FailureMessage message, int index, int total)
        {
            var ids = (message.FailingElementIds ?? new List<long>()).Concat(message.AdditionalElementIds ?? new List<long>()).Distinct().ToList();
            var text = new StringBuilder();
            text.AppendLine(index.ToString("n0", CultureInfo.CurrentCulture) + " / " + total.ToString("n0", CultureInfo.CurrentCulture));
            text.AppendLine(string.IsNullOrWhiteSpace(message.Severity) ? Unknown() : message.Severity);
            if (!string.IsNullOrWhiteSpace(message.DefinitionId)) text.AppendLine(message.DefinitionId);
            text.AppendLine(string.IsNullOrWhiteSpace(message.Description) ? Unknown() : message.Description);
            text.AppendLine(ids.Count == 0 ? L.T("failure.detail.noElements") : L.T("failure.detail.elements", ("ids", string.Join(", ", ids))));
            if (!message.Complete || message.TextTruncated) text.AppendLine(L.T("failure.detail.incomplete"));
            foreach (var snapshot in message.Snapshots ?? new List<FailureElementSnapshot>())
                text.AppendLine(snapshot.ElementId + " · " + snapshot.Category + " · " + snapshot.Type + " · " + snapshot.Level
                    + " · existsAfterRollback=" + (snapshot.ExistsAfterRollback?.ToString() ?? Unknown()));
            try { _dialogs.Copy(text.ToString().TrimEnd()); SetBanner(L.T("failure.copied")); }
            catch (Exception ex) { ShowFailure(ex.Message); }
        }

    }
}
