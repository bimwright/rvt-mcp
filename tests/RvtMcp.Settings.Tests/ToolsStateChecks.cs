using System;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using RvtMcp.Plugin.Presentation.Settings;
using RvtMcp.Plugin.Views.Settings;
using RvtMcp.ToolCatalog;

internal static class ToolsStateChecks
{
    internal static void Run()
    {
        var source = new Source();
        var model = new ToolsViewModel(source);
        var window = new SettingsWindow(new FakeSettings(), model);
        window.Show();
        try
        {
            Pump();
            Expect(source.Reads == 1 && model.Rows.Count == 2 && model.BuiltInCount == 2, "initial snapshot counts");
            window.SelectTab(SettingsTab.Tools);
            Pump();
            Expect(source.Reads == 2, "entering Tools refreshes once");
            var grid = (DataGrid)typeof(SettingsWindow).GetField("_toolsGrid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            grid.SelectedItem = model.Rows.Single(row => row.Name == "beta");
            Expect(model.SelectedTool.Name == "beta", "grid selection updates VM");
            var old = grid.SelectedItem;
            source.Publish();
            Pump();
            Expect(model.SelectedTool?.Name == "beta" && ((ToolRow)grid.SelectedItem)?.Name == "beta"
                && !ReferenceEquals(old, grid.SelectedItem), "refresh preserves selection by stable name with new row instances");
            model.SearchText = "aLPHa";
            Pump();
            Expect(model.Rows.Count == 2, "case-insensitive search includes matching names and descriptions");
            model.SearchText = "zULu";
            Expect(model.Rows.Count == 1 && model.Rows[0].Name == "alpha", "case-insensitive description search");
            model.SearchText = "unmatched";
            Expect(model.Rows.Count == 0 && model.StatusText.Length > 0, "no-match status");
            model.SearchText = "";
            Pump();
            Expect(model.SelectedTool?.Name == "beta" && grid.SelectedItem == model.SelectedTool, "filter removal restores selected key");
            model.SortBy("Description");
            Expect(model.Rows[0].Name == "beta" && model.Rows.Select(row => row.No).SequenceEqual(new[] { 1, 2 }), "description sort renumbers rows");
            var key = model.SelectedTool.Name;
            source.Status = ToolCatalogStatus.Invalid;
            source.Publish();
            Expect(model.Rows.Count == 0 && model.StatusHelpText == "rejected fixture", "invalid source clears rows and reports error");
            source.Status = ToolCatalogStatus.Current;
            source.Publish();
            Pump();
            Expect(model.SelectedTool?.Name == key, "catalog restoration preserves selection key");
            var late = source.Callback;
            window.CloseForShutdown();
            var reads = source.Reads;
            late?.Invoke(null, EventArgs.Empty);
            model.RefreshCommand.Execute(null);
            Expect(source.Reads == reads && source.Subscribers == 0 && source.Disposals == 1,
                "close detaches source, disables refresh and rejects late notification");
        }
        finally { if (window.IsLoaded) window.CloseForShutdown(); }
        Console.WriteLine("PASS: real ToolsViewModel and grid snapshot/search/sort/selection/status/lifetime");
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Source : IToolsCatalogSource
    {
        public event EventHandler Changed;
        public EventHandler Callback => Changed;
        public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
        public int Reads, Disposals;
        public ToolCatalogStatus Status = ToolCatalogStatus.Current;
        public ToolsCatalogSnapshot Read()
        {
            Reads++;
            return new ToolsCatalogSnapshot { Status = Status, Error = "rejected fixture",
                BuiltInCount = Status == ToolCatalogStatus.Current ? 2 : 0, CataloguedBuiltInCount = 10,
                Rows = Status == ToolCatalogStatus.Current ? new[] {
                    new ToolRow { Name = "alpha", Description = "Zulu", Source = "Built-in" },
                    new ToolRow { Name = "beta", Description = "Alpha", Source = "Built-in" },
                } : new ToolRow[0] };
        }
        public void Publish() => Changed?.Invoke(this, EventArgs.Empty);
        public void Dispose() { Disposals++; }
    }
}
