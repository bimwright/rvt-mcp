using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Presentation.History;
using RvtMcp.Plugin.Views;

internal static class HistoryStateChecks
{
    internal static void Run()
    {
        var runtime = new Runtime();
        var dialogs = new Dialogs();
        using (var vm = new ActivityHistoryViewModel(runtime, dialogs))
        {
            var first = new McpCallEntry { Index = 1, ToolName = "create_level", ParamsJson = "{\"name\":\"first\"}", Success = true };
            var second = new McpCallEntry { Index = 2, ToolName = "get_current_view_info", ParamsJson = "{}", ErrorMessage = "fixture", Success = false };
            runtime.Entries.Add(first); runtime.Entries.Add(second);
            vm.SearchText = "FIRST";
            Expect(vm.Matches(first) && !vm.Matches(second), "search matches raw params without case sensitivity");
            vm.SearchText = ""; vm.ResultFilter = "failed";
            Expect(!vm.Matches(first) && vm.Matches(second), "result filtering");
            vm.ResultFilter = "all"; vm.KindFilter = "write";
            Expect(vm.Matches(first) && !vm.Matches(second), "read/write filtering");
            vm.KindFilter = "all";
            vm.SelectedEntry = first;
            var task = vm.RerunCommand.ExecuteAsync();
            Expect(runtime.Requests == 1 && vm.RerunCommand.IsRunning && !vm.RerunCommand.CanExecute(null), "one request enters busy state");
            vm.SelectedEntry = second;
            vm.RerunCommand.Execute(null);
            Expect(runtime.Requests == 1 && runtime.LastRequest.ToolName == "create_level" && runtime.LastRequest.ParamsJson.Contains("first"), "busy/selection changes cannot replace frozen request");
            runtime.Complete(); PumpUntil(task);
            Expect(!vm.RerunCommand.IsRunning && vm.RerunOutput.OriginalIndex == 1, "completion reports original row");
            vm.SelectedEntry = first;
            runtime.Next = new TaskCompletionSource<HistoryRerunOutput>();
            task = vm.RerunCommand.ExecuteAsync();
            runtime.Next.SetException(new InvalidOperationException("fixture dispatch error")); PumpUntil(task);
            Expect(!vm.RerunCommand.IsRunning && vm.RerunError != null && runtime.Requests == 2, "failure is observed without retry");
            vm.SelectedEntry = new McpCallEntry { ToolName = "send_code_to_revit", CodeSnippet = "return 1;", ParamsJson = "{}" };
            dialogs.Accept = false;
            PumpUntil(vm.RerunCommand.ExecuteAsync());
            Expect(dialogs.RerunConfirms == 1 && runtime.Requests == 2, "cancelled send-code confirmation dispatches nothing");
            vm.SelectedEntry = new McpCallEntry { ToolName = "send_code_to_revit", ParamsJson = "{\"code_hash\":\"fixture\"}" };
            Expect(vm.CanRerun(vm.SelectedEntry), "redacted code is resolved through journal boundary");
            dialogs.Accept = true;
            runtime.Next = new TaskCompletionSource<HistoryRerunOutput>();
            task = vm.RerunCommand.ExecuteAsync();
            Expect(dialogs.LastRedacted && runtime.LastRequest.ParamsJson.Contains("return 2;"), "redacted rerun passes recovered code after confirmation");
            runtime.Complete(); PumpUntil(task);
            Expect(!vm.CanRerun(new McpCallEntry { IsHistorical = true }) && !vm.CanRerun(new McpCallEntry { ParamsTruncated = true }), "historical/truncated records are never runnable");
            vm.LoadPastCommand.Execute(null); vm.LoadPastCommand.Execute(null);
            Expect(runtime.Loads == 1 && !vm.LoadPastCommand.CanExecute(null), "past sessions load only once");
            dialogs.Accept = false; vm.NewSessionCommand.Execute(null);
            Expect(runtime.Clears == 0, "new-session cancellation preserves entries");
            dialogs.Accept = true; vm.NewSessionCommand.Execute(null);
            Expect(runtime.Clears == 1 && vm.SelectedEntry == null, "confirmed new session clears selection");
        }
        var lateRuntime = new Runtime();
        var lateVm = new ActivityHistoryViewModel(lateRuntime, dialogs);
        lateVm.SelectedEntry = new McpCallEntry { ToolName = "create_level", ParamsJson = "{}" };
        var late = lateVm.RerunCommand.ExecuteAsync();
        lateVm.Dispose();
        lateRuntime.Complete(); PumpUntil(late);
        Expect(lateRuntime.Requests == 1 && lateVm.RerunOutput == null && lateRuntime.Disposals == 1, "disposing UI does not replay or present an already-sent operation");

        var windowRuntime = new Runtime();
        windowRuntime.Entries.Add(new McpCallEntry { Index = 1, ToolName = "create_level", ParamsJson = "{}", Success = true });
        var window = new HistoryWindow(windowRuntime, dialogs);
        try
        {
            window.Show(); Pump();
            var shell = (HistoryShellViewModel)window.DataContext;
            var view = Field<ActivityHistoryView>(window, "_activitiesView");
            var button = Field<Button>(view, "_rerunButton");
            Expect(button.IsEnabled && shell.Activities.SelectedEntry != null, "real activity selection enables rerun command");
            var status = Field<TextBlock>(window, "_shellStatus");
            Expect(status.Text == shell.Activities.StatusText, "Activities status reaches shell");
            window.ShowIssues(); Pump();
            Expect(shell.SelectedTab == HistoryTab.Issues, "host entry selects Issues tab");
            var issueStatus = status.Text;
            shell.Activities.SelectedEntry = new McpCallEntry { Index = 2, Success = false };
            Pump();
            Expect(status.Text == issueStatus, "hidden Activities status cannot overwrite Issues status");
            shell.SelectedTab = HistoryTab.Activities; Pump();
            Expect(status.Text == shell.Activities.StatusText, "returning to Activities restores its own status");
            window.Close(); Pump();
            Expect(!window.IsVisible && windowRuntime.Deactivations > 0, "normal close hides and deactivates observations");
            window.Show(); Pump();
            Expect(windowRuntime.Activations == 2, "reopen reactivates once");
        }
        finally { window.CloseForShutdown(); }
        Expect(windowRuntime.Disposals == 1 && windowRuntime.Subscribers == 0, "terminal close disposes the shell and child runtime once");
        Console.WriteLine("PASS: History VM filters, frozen/single rerun, confirmation, error, late completion and real tab/status/lifetime wiring");
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void PumpUntil(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(5)) Pump();
        Expect(task.IsCompleted, "async fixture completed within timeout");
        task.GetAwaiter().GetResult();
    }
    private sealed class Dialogs : IHistoryDialogs
    {
        public bool Accept = true, LastRedacted;
        public int RerunConfirms;
        public bool ConfirmNewSession() => Accept;
        public bool ConfirmRerun(bool redacted) { LastRedacted = redacted; RerunConfirms++; return Accept; }
    }
    internal sealed class Runtime : IHistoryRuntime
    {
        public ObservableCollection<McpCallEntry> Entries { get; } = new ObservableCollection<McpCallEntry>();
        public event EventHandler LanguageChanged;
        public int Subscribers => LanguageChanged?.GetInvocationList().Length ?? 0;
        public int LanguageVersion => 1;
        public int Requests, Loads, Clears, Disposals, Activations, Deactivations;
        public HistoryRerunRequest LastRequest;
        public TaskCompletionSource<HistoryRerunOutput> Next = new TaskCompletionSource<HistoryRerunOutput>();
        public Task<HistoryRerunOutput> Rerun(HistoryRerunRequest request) { Requests++; LastRequest = request; return Next.Task; }
        public void Complete() => Next.SetResult(new HistoryRerunOutput { OriginalIndex = LastRequest.OriginalIndex,
            Entry = new McpCallEntry { Success = true, ToolName = LastRequest.ToolName, ResultJson = "{}" }, CompletedAt = DateTime.Now });
        public void Activate() { Activations++; }
        public void Deactivate() { Deactivations++; }
        public int LoadPastSessions() { Loads++; return 0; }
        public void ClearSession() { Clears++; Entries.Clear(); }
        public void RefreshSummaries() { }
        public string Description(string tool) => tool;
        public string FindCode(string hash) => hash == "fixture" ? "return 2;" : null;
        public void OpenLogs() { }
        public void Dispose() { Disposals++; }
    }
}
