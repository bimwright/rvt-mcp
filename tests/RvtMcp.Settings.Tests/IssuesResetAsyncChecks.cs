using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RvtMcp.Failures;
using RvtMcp.Plugin.Presentation.Issues;
using RvtMcp.Plugin.Views;

internal static class IssuesResetAsyncChecks
{
    internal static void Run()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var bindings = new WpfBindingErrors();
        try
        {
            CheckResponsiveWindow();
            foreach (var phase in new[] { "summary-error", "reset-error", "busy", "cancel", "dispose-summary", "dispose-reset" })
                CheckCompletion(phase);
            bindings.AssertClean();
            Console.WriteLine("PASS: async Reset UI responsiveness, progress, single flight, busy/cancel/errors, retry and disposal");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void CheckResponsiveWindow()
    {
        var runtime = new IssuesStateChecks.Runtime {
            SummaryWork = new TaskCompletionSource<FailureLedgerSummary>(), ResetWork = new TaskCompletionSource<string>() };
        using var view = new FailureIssuesView(runtime, new IssuesStateChecks.Dialogs());
        var window = new Window { Content = view, Width = 1000, Height = 700, ShowInTaskbar = false };
        try
        {
            window.Show(); Pump();
            var vm = view.ViewModel;
            var uiThread = Environment.CurrentManagedThreadId;
            vm.PropertyChanged += (s, e) => Check(Environment.CurrentManagedThreadId == uiThread, "VM notifications stay on UI dispatcher");
            var before = runtime.PageQueries;
            var task = vm.ResetCommand.ExecuteAsync();
            Check(vm.IsResetting && !view.IsEnabled && !task.IsCompleted, "summary wait disables Issues without blocking caller");
            Check(!vm.RefreshCommand.CanExecute(null) && !vm.ResetCommand.CanExecute(null), "refresh and duplicate reset are disabled");
            vm.ResetCommand.Execute(null); vm.RefreshCommand.Execute(null); vm.Refresh();
            Check(runtime.Summaries == 1 && runtime.PageQueries == before, "waiting reset cannot trigger overlapping summary or queries");
            var callbacks = 0;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => callbacks++), DispatcherPriority.Background);
            Pump();
            Check(callbacks == 1 && !task.IsCompleted, "dispatcher processes work while summary is pending");
            runtime.SummaryWork.SetResult(new FailureLedgerSummary()); Pump();
            Check(vm.IsResetting && runtime.Resets == 1 && !task.IsCompleted && !view.IsEnabled, "backup wait retains disabled controls");
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => callbacks++), DispatcherPriority.Background); Pump();
            Check(callbacks == 2, "dispatcher processes work while backup is pending");
            runtime.Incidents.Clear(); runtime.ResetWork.SetResult("fixture-backup.db"); PumpUntil(task);
            Check(!vm.IsResetting && view.IsEnabled && vm.Rows.Count == 0 && vm.RefreshCommand.CanExecute(null), "successful reset refreshes and re-enables the view");
        }
        finally { window.Close(); }
    }

    private static void CheckCompletion(string phase)
    {
        var runtime = new IssuesStateChecks.Runtime {
            SummaryWork = new TaskCompletionSource<FailureLedgerSummary>(), ResetWork = new TaskCompletionSource<string>() };
        var dialogs = new IssuesStateChecks.Dialogs { Accept = phase != "cancel" };
        using var vm = new IssuesViewModel(runtime, dialogs);
        vm.Activate();
        var before = vm.PageStatus;
        var task = vm.ResetCommand.ExecuteAsync();
        if (phase == "dispose-summary") vm.Dispose();
        if (phase == "busy") runtime.CanReset = false;
        if (phase == "summary-error") runtime.SummaryWork.SetException(new IOException("fixture summary error"));
        else runtime.SummaryWork.SetResult(new FailureLedgerSummary());
        Pump();
        if (phase == "reset-error") runtime.ResetWork.SetException(new IOException("fixture backup error"));
        if (phase == "dispose-reset") { vm.Dispose(); runtime.ResetWork.SetResult("fixture-backup.db"); }
        PumpUntil(task);
        Check(runtime.Incidents.Count == 1, "cancel/error/disposal preserves fixture records / " + phase);
        if (phase == "dispose-summary") Check(dialogs.ResetConfirms == 0 && runtime.Resets == 0, "disposed summary never confirms or resets");
        else if (phase == "dispose-reset") Check(runtime.Disposals == 1 && runtime.PageQueries == 1, "late completed reset does not refresh disposed UI");
        else
        {
            Check(!vm.IsResetting && vm.ResetCommand.CanExecute(null), "busy state clears for retry / " + phase);
            if (phase == "cancel") Check(runtime.Resets == 0 && vm.PageStatus == before, "cancel restores previous status");
            if (phase == "busy") Check(runtime.Resets == 0 && dialogs.ResetConfirms == 0, "activity appearing during summary prevents confirmation");
            runtime.CanReset = true; dialogs.Accept = false; runtime.SummaryWork = null;
            PumpUntil(vm.ResetCommand.ExecuteAsync());
            Check(!vm.IsResetting, "subsequent reset can be started after previous completion");
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void PumpUntil(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5)) Pump();
        Check(task.IsCompleted, "asynchronous fixture completes within timeout");
        task.GetAwaiter().GetResult();
    }
}
