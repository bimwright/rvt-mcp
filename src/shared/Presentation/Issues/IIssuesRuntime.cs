using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RvtMcp.Failures;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Presentation.Issues
{
    internal interface IIssuesRuntime : IDisposable
    {
        event EventHandler ContextChanged;
        event EventHandler LanguageChanged;
        void Activate();
        void Deactivate();
        IReadOnlyList<FailureIssueModel> Models(string state);
        FailureRecordPage Query(FailureRecordQuery query);
        FailureOperation Operation(string id);
        IReadOnlyList<FailureRule> Rules(string scope);
        FailureIssuesContext Context(string scope = null);
        ICollection<string> OpenScopes();
        FailureGate Gate(string scope);
        FailurePersistenceResult Save(FailureDecision decision);
        bool CanReset { get; }
        Task<FailureLedgerSummary> ResetSummaryAsync();
        string BackupFolder { get; }
        Task<string> ResetAsync();
        string WindowsUser { get; }
    }

    internal interface IIssuesDialogs
    {
        bool ConfirmDecision(IssueDecisionReview review);
        bool ConfirmReset(FailureLedgerSummary summary, string backupFolder);
        void Copy(string text);
    }

    internal sealed class IssueDecisionReview
    {
        internal string Action, Warning, ConstraintsHeading, Constraints, Details, Reason;
    }
}
