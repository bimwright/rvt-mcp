using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RvtMcp.Failures;
using RvtMcp.Plugin.Views;

// Only the host/storage boundaries are fake: HistoryWindow and FailureIssuesView
// compile unchanged. Unexpected dispatch/file-history access must fail, not reach Revit or disk.
namespace Autodesk.Revit.UI
{
    public sealed class ExternalEvent
    {
        public void Raise() => throw new NotSupportedException("No Revit dispatch in UI tests.");
    }
}

namespace RvtMcp.Plugin
{
    public sealed class CommandDispatcher
    {
        public TestCommand GetCommand(string name) => null;
    }
    public sealed class TestCommand { public string Description { get; set; } }
    public sealed class McpEventHandler
    {
        public void Enqueue(PendingRequest request) => throw new NotSupportedException("No Revit dispatch in UI tests.");
    }
    public sealed class PendingRequest
    {
        public string Id, CommandName, ParamsJson;
        public TaskCompletionSource<string> Tcs;
    }
    public sealed class CommandResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public object Data { get; set; }
    }
    public sealed class McpSessionLog
    {
        public ObservableCollection<McpCallEntry> Entries { get; } = new ObservableCollection<McpCallEntry>();
        public void Clear() => Entries.Clear();
        public void Add(McpCallEntry entry) => Entries.Add(entry);
        internal static void RefreshSummary(McpCallEntry entry) { }
    }
    public sealed class McpCallEntry
    {
        public int Index { get; set; }
        public DateTime Timestamp { get; set; }
        public string ToolName { get; set; }
        public bool Success { get; set; }
        public long DurationMs { get; set; }
        public string ParamsJson { get; set; }
        public string ErrorMessage { get; set; }
        public string CodeSnippet { get; set; }
        public string ResultJson { get; set; }
        public string Summary { get; set; }
        public string ToolDescription { get; set; }
        public int? RerunOfIndex { get; set; }
        public bool ParamsTruncated { get; set; }
        public bool IsHistorical { get; set; }
        public string SessionTag { get; set; }
        public string TimeLabel => "12:00:00";
    }
    public static class McpLogger
    {
        public static string CurrentLogPath => null;
        public static string LocalAppDataOverride => null;
        public static string CurrentSessionId => "ui-test";
    }
    public static class SessionLogHistoryLoader
    {
        public static List<McpCallEntry> LoadPastSessions(string folder, string session, int count) =>
            throw new NotSupportedException("No file history in UI tests.");
    }
    public static class SendCodeJournal
    {
        public static string TryFindCodeByHash(string hash) => null;
    }
    public static class SummaryGenerator
    {
        public static string Generate(string tool, string input, string output, bool success, string error) => tool;
    }
}

namespace RvtMcp.Plugin.Failures
{
    internal static class FailureControl
    {
        internal static FailureIssuesContext Context = new FailureIssuesContext();
        internal static FailureIssuesContext SelectedContext;
        internal static FailureLedgerStore Store = new FailureLedgerStore();
        internal static object Active => null;
        internal static FailureDecision LastDecision;
        internal static event EventHandler UiContextChanged;
        internal static FailureIssuesContext GetUiContext() => Context;
        internal static FailureIssuesContext GetUiContext(string scope) =>
            Context.IsAvailable && Context.ModelScopeKey == scope ? Context : SelectedContext ?? new FailureIssuesContext();
        internal static HashSet<string> CopyOpenModelScopes() => new HashSet<string>(new[] { Context.ModelScopeKey ?? "" });
        internal static FailureGate GetUiGate(string scope) => new FailureGate();
        internal static FailurePersistenceResult ApplyUiDecision(FailureDecision decision)
        {
            LastDecision = decision;
            return new FailurePersistenceResult { Persistence = "recorded", Durable = true };
        }
        internal static FailureLedgerSummary GetResetSummary() => new FailureLedgerSummary();
        internal static string ResetLedger() => throw new NotSupportedException("No ledger reset in UI tests.");
        internal static void SetContext(FailureIssuesContext context)
        {
            Context = context;
            UiContextChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}

namespace RvtMcp.Failures
{
    public sealed class FailureLedgerStore
    {
        internal static List<FailureIssueModel> Models = new List<FailureIssueModel>();
        internal static List<FailureIncident> Incidents = new List<FailureIncident>();
        internal static FailureOperation Operation;
        public string BackupFolder => "in-memory";
        public FailureLedgerStore(bool readOnly = false) { }
        public List<FailureIssueModel> GetIssueModels(string state = null) => Models;
        public FailureRecordPage Query(FailureRecordQuery query) => new FailureRecordPage {
            Available = true, Records = Incidents.Where(i => i.ModelScopeKey == query.ModelScopeKey
                && (query.IncidentId == null || i.IncidentId == query.IncidentId)
                && (query.State == null || i.State == query.State)
                && (!query.RevitYear.HasValue || i.RevitYear == query.RevitYear)).ToList()
        };
        public FailureOperation GetOperation(string id) => Operation;
        public List<FailureRule> GetRules(string scope) => new List<FailureRule>();
    }
}
