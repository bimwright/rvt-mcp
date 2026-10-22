using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Newtonsoft.Json;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Infrastructure.UI;
using RvtMcp.Plugin.Presentation.History;
using RvtMcp.Plugin.Views;

internal static class HistoryLatestChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "history-latest-" + Guid.NewGuid().ToString("N"));
        using var bindings = new WpfBindingErrors();
        try
        {
            McpLogger.LocalAppDataOverride = root;
            McpLogger.Initialize(enabled: true);
            var path = McpLogger.CurrentLogPath;
            var timestamp = DateTime.UtcNow.AddHours(-1);
            File.WriteAllLines(path, Enumerable.Range(1, 250).Select(i => JsonConvert.SerializeObject(new
            {
                timestamp = timestamp.AddSeconds(i).ToString("O"), session_id = "previous", call_id = "past-" + i,
                tool = "get_fixture_" + i, success = true, duration_ms = i
            })));
            var log = new McpSessionLog();
            var live = new McpCallEntry { CallId = "live-one", ToolName = "get_live", Timestamp = DateTime.Now, Success = true };
            log.Add(live);
            McpLogger.Log(live.ToolName, "{}", true, 1, callId: live.CallId);
            var dialogs = new Dialogs();
            using var vm = new ActivityHistoryViewModel(new HistoryRuntime(log, new CommandDispatcher(), null, null), dialogs);
            using var view = new ActivityHistoryView(vm);
            var window = new Window { Content = view, Width = 1000, Height = 700, ShowInTaskbar = false };
            try
            {
                window.Show(); Pump();
                vm.LoadPastCommand.Execute(null); Pump();
                Check(log.Entries.Count == 200 && log.Count == 1, "merged latest snapshot caps the whole table at 200 and retains live row");
                Check(log.Entries.Select(e => e.CallId).Distinct().Count() == 200, "file/live overlap is deduplicated by execution identity");
                Check(log.Entries.Contains(live) && live.Index == 1, "live object and rerun index survive refresh");
                var grid = Field<DataGrid>(view, "_grid");
                grid.SelectedItem = live; Pump();
                vm.LoadPastCommand.Execute(null); Pump();
                Check(log.Entries.Count == 200 && ReferenceEquals(vm.SelectedEntry, live) && vm.LoadPastCommand.CanExecute(null),
                    "repeat refresh neither duplicates nor replaces retained selection and remains enabled");
                CheckRowNumber(grid, 0);
                CheckRowNumber(grid, 150);
                vm.SearchText = "get_fixture_24"; Pump();
                Check(grid.Items.Count == 10, "filter applies to the refreshed snapshot");
                CheckRowNumber(grid, 0);
                grid.Items.SortDescriptions.Clear();
                grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Timestamp", System.ComponentModel.ListSortDirection.Ascending));
                Pump(); CheckRowNumber(grid, 0); CheckRowNumber(grid, 8);
                vm.SearchText = ""; Pump();
                var before = SHA256.HashData(File.ReadAllBytes(path));
                dialogs.Accept = false;
                vm.NewSessionCommand.Execute(null); Pump();
                Check(log.Entries.Count == 200, "cancelled New Session preserves all rows");
                dialogs.Accept = true;
                vm.NewSessionCommand.Execute(null); Pump();
                Check(log.Entries.Count == 0 && grid.Items.Count == 0 && vm.SelectedEntry == null,
                    "New Session clears mixed live/history and selection in the real grid");
                Check(Field<ContentControl>(view, "_inputContent").Content == null
                    && Field<StackPanel>(view, "_outputContainer").Children.Count == 0, "New Session clears detail and output");
                Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "New Session preserves the file log byte-for-byte");
                vm.LoadPastCommand.Execute(null); Pump();
                Check(log.Entries.Count == 200 && vm.LoadPastCommand.CanExecute(null), "New Session allows reloading preserved file history");
                vm.NewSessionCommand.Execute(null);
                log.Add(new McpCallEntry { ToolName = "get_next" });
                Check(log.Entries.Single().Index == 1, "first live call after reset retains positive session identity");
                vm.NewSessionCommand.Execute(null);
                File.WriteAllText(path, "");
                vm.LoadPastCommand.Execute(null);
                Check(log.Entries.Count == 0 && vm.LoadPastCommand.CanExecute(null), "zero-row refresh remains enabled");
                McpLogger.Log("get_added", "{}", true, 1);
                vm.LoadPastCommand.Execute(null);
                Check(log.Entries.Count == 1, "refresh discovers commands appended after an empty load");
            }
            finally { window.Close(); }
            CheckNativeRerunRecording();
            foreach (var failure in new[] { false, true }) CheckResetWhileRerunning(failure);
            bindings.AssertClean();
            Console.WriteLine("PASS: latest 200, refresh/deduplication, real grid numbering/filter/sort/scroll, New Session/file preservation and pending reruns");
        }
        finally
        {
            McpLogger.Initialize(false);
            McpLogger.LocalAppDataOverride = null;
            Directory.Delete(root, true);
        }
    }

    private static void CheckNativeRerunRecording()
    {
        var log = new McpSessionLog();
        PendingRequest pending = null;
        var handler = new McpEventHandler { OnEnqueue = request => pending = request };
        var dispatcher = Dispatcher.CurrentDispatcher;
        var external = new Autodesk.Revit.UI.ExternalEvent { OnRaise = () => dispatcher.Invoke(() =>
        {
            log.Add(new McpCallEntry { CallId = pending.LogId, ToolName = pending.CommandName, Success = true });
            pending.Tcs.SetResult("{\"success\":true,\"data\":{}}");
        }) };
        using var runtime = new HistoryRuntime(log, new CommandDispatcher(), handler, external);
        var task = runtime.Rerun(new HistoryRerunRequest { ToolName = "get_current_view_info", ParamsJson = "{}", OriginalIndex = 42 });
        PumpUntil(task);
        Check(log.Entries.Count == 1 && log.Entries.Single().RerunOfIndex == 42, "native rerun adapter annotates the handler row instead of logging it twice");
    }

    private static void CheckResetWhileRerunning(bool failure)
    {
        var runtime = new HistoryStateChecks.Runtime();
        using var vm = new ActivityHistoryViewModel(runtime, new Dialogs());
        vm.SelectedEntry = new McpCallEntry { Index = 1, ToolName = "get_current_view_info", ParamsJson = "{}" };
        var task = vm.RerunCommand.ExecuteAsync();
        vm.NewSessionCommand.Execute(null);
        if (failure) runtime.Next.SetException(new InvalidOperationException("fixture pending error"));
        else runtime.Complete();
        PumpUntil(task);
        Check(runtime.Requests == 1 && vm.RerunOutput == null && vm.RerunError == null && vm.StatusText == "",
            "late native completion does not restore pre-reset details or retry / failure=" + failure);
    }

    private static void CheckRowNumber(DataGrid grid, int index)
    {
        grid.ScrollIntoView(grid.Items[index]); grid.UpdateLayout(); Pump();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
        var text = (TextBlock)grid.Columns[0].GetCellContent(row);
        Check(text != null && text.Text == (index + 1).ToString(), "display row number equals visible index + 1 / " + index);
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void PumpUntil(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(5)) Pump();
        Check(task.IsCompleted, "async operation completes within fixture timeout");
        task.GetAwaiter().GetResult();
    }
    private sealed class Dialogs : IHistoryDialogs
    {
        public bool Accept = true;
        public bool ConfirmNewSession() => Accept;
        public bool ConfirmRerun(bool redacted) => false;
    }
}
