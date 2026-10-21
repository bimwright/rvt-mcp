using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using RvtMcp.Plugin.Presentation.History;
using RvtMcp.Failures;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Presentation.Issues;
using RvtMcp.Plugin.Views;

internal static class IssuesStateChecks
{
    internal static void Run()
    {
        var assembly = typeof(IssuesStateChecks).Assembly;
        var english = EmbeddedCatalog.Load(assembly, "en");
        L.ForceSyncBuilds = true;
        L.Initialize("English", "en", locale => StringTable.Build(locale, english, EmbeddedCatalog.Load(assembly, locale), null), null);
        try { RunCore(); }
        finally { L.ResetForTests(); }
    }
    private static void RunCore()
    {
        var runtime = new Runtime();
        var dialogs = new Dialogs();
        using (var vm = new IssuesViewModel(runtime, dialogs))
        {
            vm.Activate(); vm.Activate();
            Expect(runtime.Activations == 1 && vm.Incident != null && vm.SelectedModel.RevitYear == 2027, "activate once and load selected scope/year");
            vm.Reason = "Review selected B";
            Expect(!vm.DecisionCommand("rework").CanExecute(null), "review acknowledgement is required");
            vm.VerifiedModel = true;
            vm.DecisionCommand("rework").Execute(null);
            Expect(runtime.Saves == 1 && dialogs.Confirms == 0 && runtime.LastDecision.ModelScopeKey == "selected-b"
                && runtime.LastDecision.IncidentId == vm.Incident.IncidentId && vm.Incident.RevitYear == 2027 && runtime.LastDecision.DocumentRevision == "recorded-b", "Rework saves selected incident without active-model substitution or new dialog");
            vm.VerifiedModel = true; vm.Reason = "keep on failure";
            runtime.SaveSucceeds = false;
            vm.DecisionCommand("abandon").Execute(null);
            Expect(vm.Reason == "keep on failure" && vm.VerifiedModel && vm.StatusTone == UiTone.Error, "failed persistence retains review input and reports failure");
            runtime.SaveSucceeds = true;
            vm.Incident.Complete = false; vm.RefreshActions();
            Expect(vm.DecisionCommand("rework").CanExecute(null) && !vm.DecisionCommand("accept_once").CanExecute(null)
                && !vm.DecisionCommand("native_grant").CanExecute(null), "incomplete capture permits closing only");
            vm.Incident.Complete = true;
            runtime.Op.Execution = "running"; vm.RefreshActions();
            Expect(!vm.DecisionCommand("rework").CanExecute(null), "running operation cannot be decided");
            runtime.Op.Execution = "completed"; vm.RefreshActions();
            dialogs.Accept = false;
            var saves = runtime.Saves;
            vm.DecisionCommand("accept_once").Execute(null);
            Expect(runtime.Saves == saves && dialogs.LastReview.Constraints.Contains("recorded-b"), "acceptance cancellation preserves storage and review shows exact revision");
            dialogs.Accept = true;
            vm.DecisionCommand("accept_once").Execute(null);
            Expect(runtime.Saves == saves + 1 && runtime.LastDecision.Action == "accept_once" && runtime.LastDecision.Grants.All(g => g.RevitYear == 2027), "confirmed warning acceptance saves once");
            vm.VerifiedModel = true;
            vm.Incident.Messages[0].Severity = "Error"; vm.RefreshActions();
            Expect(!vm.DecisionCommand("accept_once").CanExecute(null) && !vm.DecisionCommand("create_rule").CanExecute(null), "errors cannot use warning acceptance");
            vm.Incident.Messages[0].Severity = "Warning"; vm.RefreshActions();
            vm.SelectedRule = vm.Rules[0];
            Expect(!vm.RevokeCommand.CanExecute(null), "revoke retains current-model requirement");
            runtime.Current = new FailureIssuesContext { IsAvailable = true, ModelScopeKey = "selected-b", RevitYear = 2027, DocumentRevision = "live-b" };
            runtime.RaiseContext();
            Expect(vm.RevokeCommand.CanExecute(null), "matching live scope enables revoke");
        }

        runtime = new Runtime(); dialogs = new Dialogs();
        using (var vm = new IssuesViewModel(runtime, dialogs))
        {
            vm.Activate();
            runtime.CanReset = false;
            vm.ResetCommand.Execute(null);
            Expect(runtime.Resets == 0 && dialogs.ResetConfirms == 0, "busy reset never asks or clears");
            runtime.CanReset = true; dialogs.Accept = false;
            vm.ResetCommand.Execute(null);
            Expect(runtime.Resets == 0 && dialogs.ResetConfirms == 1, "missing acknowledgement/cancel never clears");
            dialogs.Accept = true; runtime.ResetBusyAtCommit = true;
            vm.ResetCommand.Execute(null);
            Expect(runtime.Resets == 1 && vm.Incident != null && vm.StatusTone == UiTone.Error, "execution-time busy reset retains records");
            runtime.ResetBusyAtCommit = false;
            vm.ResetCommand.Execute(null);
            Expect(runtime.Resets == 2 && vm.Rows.Count == 0 && dialogs.BackupFolder == "fixture-backups", "acknowledged reset uses broad service and refreshes emptied records");
        }

        runtime = new Runtime(); dialogs = new Dialogs();
        runtime.Incidents[0].State = "observed";
        runtime.Incidents[0].OperationContext = "open:ignore_unresolved_references";
        runtime.Incidents[0].Messages[0].Severity = "TaskDialog";
        runtime.Incidents[0].Messages[0].DefinitionId = "TaskDialog_Unresolved_References";
        using (var vm = new IssuesViewModel(runtime, dialogs))
        {
            vm.State = "observed"; vm.Activate(); vm.VerifiedModel = true;
            vm.DecisionCommand("create_rule").Execute(null);
            Expect(runtime.Saves == 0 && dialogs.Confirms == 0, "supported dialog still requires explicit semantic consent");
            vm.DialogConsent = true;
            vm.DecisionCommand("create_rule").Execute(null);
            Expect(runtime.Saves == 1 && dialogs.Confirms == 1, "consented reviewed dialog rule uses confirmation and one save");
        }

        runtime = new Runtime();
        var first = runtime.Incidents[0];
        runtime.Incidents = Enumerable.Range(0, 620).Select(i => new FailureIncident { IncidentId = "page-" + i,
            ModelScopeKey = first.ModelScopeKey, RevitYear = first.RevitYear, OperationId = first.OperationId,
            CreatedUtc = DateTimeOffset.UtcNow.AddSeconds(i), State = "open" }).ToList();
        var bounded = new IssuesViewModel(runtime, new Dialogs());
        bounded.Activate();
        Expect(bounded.Rows.Count == 500 && bounded.Models[0].Source.IssueCount == 620 && runtime.PageQueries == 5,
            "bounded newest rows do not truncate model counts");
        Expect(runtime.Queries.Where(q => !q.IncludeDetails).All(q => q.NewestFirst && q.ModelScopeKey == "selected-b" && q.RevitYear == 2027 && q.State == "open"),
            "every page pins scope/year/state and newest direction");
        var late = runtime.ContextCallback;
        var queries = runtime.PageQueries;
        bounded.Deactivate(); late?.Invoke(null, EventArgs.Empty);
        bounded.Dispose(); bounded.Dispose(); late?.Invoke(null, EventArgs.Empty);
        Expect(runtime.PageQueries == queries && runtime.Subscribers == 0 && runtime.Disposals == 1 && !bounded.ResetCommand.CanExecute(null),
            "deactivate/dispose detach source and reject late actions");
        CheckSharedWindow();
        Console.WriteLine("PASS: Issues VM scope/year/revision, review/consent/persistence, reset cancel/busy/commit, paging and lifetime");
    }

    private static void CheckSharedWindow()
    {
        using var bindings = new WpfBindingErrors();
        var issues = new Runtime();
        var activities = new HistoryStateChecks.Runtime();
        var view = new FailureIssuesView(issues, new Dialogs());
        var window = new HistoryWindow(activities, null, view);
        Action pump = () => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show(); pump();
            var shell = (HistoryShellViewModel)window.DataContext;
            window.ShowIssues(); pump();
            Expect(issues.Activations == 1 && issues.PageQueries == 1, "first Issues navigation activates and loads once: " + issues.Activations + "/" + issues.PageQueries);
            var selected = view.ViewModel.SelectedModel;
            var picker = (ComboBox)typeof(FailureIssuesView).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(view);
            // A child selector's bubbling event must not navigate the tab shell.
            Expect(picker != null, "real model picker is present");
            picker.RaiseEvent(new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new object[0], new object[0]));
            Expect(shell.SelectedTab == HistoryTab.Issues, "child selection does not change selected tab");
            view.ViewModel.VerifiedModel = true;
            Expect(shell.StatusText == view.ViewModel.StatusText && shell.StatusTone == view.ViewModel.StatusTone, "Issues status and review tone reach shared shell");
            shell.SelectedTab = HistoryTab.Activities; pump();
            var activityStatus = shell.StatusText;
            view.ViewModel.Reason = "hidden issue reason";
            Expect(shell.StatusText == activityStatus, "hidden Issues status cannot overwrite Activities");
            shell.SelectedTab = HistoryTab.Issues; pump();
            Expect(view.ViewModel.SelectedModel.ModelScopeKey == selected.ModelScopeKey && view.ViewModel.SelectedModel.RevitYear == selected.RevitYear,
                "tab navigation preserves selected model scope and year");
            var activations = issues.Activations;
            window.Close(); pump();
            Expect(!window.IsVisible && issues.Deactivations > 0, "hide deactivates Issues observations");
            window.Show(); pump();
            Expect(issues.Activations == activations + 1, "reopen activates Issues exactly once");
        }
        finally { window.CloseForShutdown(); pump(); }
        Expect(issues.Disposals == 1 && issues.Subscribers == 0 && activities.Disposals == 1 && activities.Subscribers == 0,
            "terminal shared-window close disposes both runtimes and observations");
        bindings.AssertClean();
        Console.WriteLine("PASS: shared History/Issues window activation, scope/year, status isolation and terminal cleanup");
    }

    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Dialogs : IIssuesDialogs
    {
        public bool Accept = true;
        public int Confirms, ResetConfirms;
        public IssueDecisionReview LastReview;
        public string BackupFolder;
        public bool ConfirmDecision(IssueDecisionReview review) { Confirms++; LastReview = review; return Accept; }
        public bool ConfirmReset(FailureLedgerSummary summary, string folder) { ResetConfirms++; BackupFolder = folder; return Accept; }
        public void Copy(string text) { }
    }
    private sealed class Runtime : IIssuesRuntime
    {
        public event EventHandler ContextChanged;
        public event EventHandler LanguageChanged;
        public EventHandler ContextCallback => ContextChanged;
        public int Subscribers => (ContextChanged?.GetInvocationList().Length ?? 0) + (LanguageChanged?.GetInvocationList().Length ?? 0);
        public int Activations, Deactivations, Saves, Resets, Disposals, PageQueries;
        public bool SaveSucceeds = true, ResetBusyAtCommit;
        public bool CanReset { get; set; } = true;
        public string BackupFolder => "fixture-backups";
        public string WindowsUser => "fixture-user";
        public FailureDecision LastDecision;
        public FailureIssuesContext Current = new FailureIssuesContext { IsAvailable = true, ModelScopeKey = "other-a", RevitYear = 2022, DocumentRevision = "unrelated" };
        public FailureOperation Op = new FailureOperation { OperationId = "operation-b", Execution = "completed", Outcome = "rolled_back", Tool = "fixture" };
        public List<FailureRecordQuery> Queries = new List<FailureRecordQuery>();
        public List<FailureIncident> Incidents = new List<FailureIncident> { new FailureIncident {
            ModelScopeKey = "selected-b", ModelTitle = "Selected B", RevitYear = 2027, DocumentRevision = "recorded-b",
            OperationId = "operation-b", OperationContext = "edit", RequestHash = "request-b", MessageCount = 1,
            NativeAction = "save_as", NativeContextHash = "native-b", NativeContextDescription = "Fixture destination",
            Messages = new List<FailureMessage> { new FailureMessage { Severity = "Warning", DefinitionId = "6ce96eb8-545c-45df-a795-068c6c8c5d72", Description = "Fixture warning" } }
        } };
        public void Activate() { Activations++; }
        public void Deactivate() { Deactivations++; }
        public IReadOnlyList<FailureIssueModel> Models(string state) => Incidents.Where(i => state == null || i.State == state)
            .GroupBy(i => new { i.ModelScopeKey, i.RevitYear }).Select(g => new FailureIssueModel { ModelScopeKey = g.Key.ModelScopeKey,
                RevitYear = g.Key.RevitYear, Title = "Selected B", IssueCount = g.Count() }).ToList();
        public FailureRecordPage Query(FailureRecordQuery query)
        {
            Queries.Add(query);
            var matching = Incidents.Where(i => i.ModelScopeKey == query.ModelScopeKey && (!query.RevitYear.HasValue || i.RevitYear == query.RevitYear)
                && (query.State == null || i.State == query.State) && (query.IncidentId == null || i.IncidentId == query.IncidentId)).OrderByDescending(i => i.CreatedUtc).ToList();
            var offset = query.Cursor == null ? 0 : int.Parse(query.Cursor);
            if (!query.IncludeDetails) PageQueries++;
            return new FailureRecordPage { Available = true, Records = matching.Skip(offset).Take(query.Limit).ToList(),
                NextCursor = offset + query.Limit < matching.Count ? (offset + query.Limit).ToString() : null };
        }
        public FailureOperation Operation(string id) => Op;
        public IReadOnlyList<FailureRule> Rules(string scope) => new[] { new FailureRule { ModelScopeKey = scope, Enabled = true } };
        public FailureIssuesContext Context(string scope = null) => scope == null || scope == Current.ModelScopeKey ? Current : new FailureIssuesContext();
        public ICollection<string> OpenScopes() => new[] { Current.ModelScopeKey };
        public FailureGate Gate(string scope) => new FailureGate();
        public FailurePersistenceResult Save(FailureDecision decision)
        {
            Saves++; LastDecision = decision;
            return new FailurePersistenceResult { Persistence = SaveSucceeds ? "recorded" : "failed", Durable = SaveSucceeds, ErrorCode = SaveSucceeds ? null : "fixture failure" };
        }
        public FailureLedgerSummary ResetSummary() => new FailureLedgerSummary { IssueCount = Incidents.Count };
        public string Reset() { Resets++; if (ResetBusyAtCommit) throw new InvalidOperationException("FAILURE_RESET_BUSY"); Incidents.Clear(); return "fixture-backup.db"; }
        public void RaiseContext() => ContextChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() { Disposals++; }
    }
}
