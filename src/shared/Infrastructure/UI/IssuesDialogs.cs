using System;
using System.Windows;
using RvtMcp.Failures;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation.Issues;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Infrastructure.UI
{
    internal sealed class IssuesDialogs : IIssuesDialogs
    {
        private readonly Func<Window> _owner;
        internal IssuesDialogs(Func<Window> owner) { _owner = owner; }
        public bool ConfirmDecision(IssueDecisionReview review) => new FailureDecisionReviewWindow(_owner(), review.Action,
            review.Warning, review.ConstraintsHeading, review.Constraints, review.Details, review.Reason).ShowDialog() == true;
        public bool ConfirmReset(FailureLedgerSummary summary, string backupFolder) =>
            new FailureResetDialog(L.T("failure.reset.confirm", ("issues", summary.IssueCount),
                ("open", summary.OpenIssueCount), ("models", summary.ModelCount), ("blocked", summary.BlockedModelCount),
                ("folder", backupFolder))) { Owner = _owner() }.ShowDialog() == true;
        public void Copy(string text) => Clipboard.SetText(text);
    }
}
