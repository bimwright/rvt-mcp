using System;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.ToolBaker;
using RvtMcp.Plugin.Presentation.Settings;
using RvtMcp.Plugin.Views.Settings;
using CatalogStore = RvtMcp.ToolCatalog.ToolCatalogStore;
using CatalogCodec = RvtMcp.ToolCatalog.ToolCatalogCodec;

namespace RvtMcp.Plugin.Infrastructure.UI
{
    public sealed class ToolsCatalogSource : IToolsCatalogSource
    {
        private readonly BakedToolRegistry _registry;
        private readonly Assembly _summaryAssembly;
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        public ToolsCatalogSource(BakedToolRegistry registry, Assembly summaryAssembly)
        {
            _registry = registry;
            _summaryAssembly = summaryAssembly ?? Assembly.GetExecutingAssembly();
            _dispatcher = Dispatcher.CurrentDispatcher;
            CatalogStore.Changed += OnChanged;
            L.Changed += OnChanged;
        }

        public event EventHandler Changed;
        public ToolsCatalogSnapshot Read()
        {
            var snapshot = new ToolsCatalogSnapshot { Status = CatalogStore.Status, Error = CatalogStore.Error };
            if (snapshot.Status == RvtMcp.ToolCatalog.ToolCatalogStatus.Current) BuildRows(CatalogStore.Current, snapshot);
            return snapshot;
        }

        private void BuildRows(RvtMcp.ToolCatalog.ToolCatalog catalog, ToolsCatalogSnapshot snapshot)
        {
            if (catalog == null) return;
            var rows = catalog.Tools.Select(entry =>
            {
                var summary = ToolSummaryCatalog.ResolveWithFallback(_summaryAssembly, L.Locale, entry.McpName, entry.ShortDescriptionEn);
                return new ToolRow
                {
                    Name = entry.McpName,
                    Description = summary.Text,
                    IsSummaryFallback = summary.IsFallback,
                    Source = SettingsText.Text("settings.tools.source.builtIn", "Built-in"),
                    TimeoutDisplay = TimeoutDisplay(entry.Timeout),
                    TimeoutHelpText = TimeoutHelp(entry.Timeout),
                };
            }).ToList();
            snapshot.BuiltInCount = rows.Count;
            snapshot.CataloguedBuiltInCount = catalog.CataloguedBuiltInCount;

            var bakedGate = catalog.Tools.Any(entry => string.Equals(entry.McpName, "revit_run_baked_tool", StringComparison.Ordinal));
            if (bakedGate && _registry != null)
            {
                var bakedTimeout = catalog.Tools.First(entry => string.Equals(entry.McpName, "revit_run_baked_tool", StringComparison.Ordinal)).Timeout;
                foreach (var meta in _registry.GetAllSortedForList())
                {
                    if (meta == null || string.IsNullOrWhiteSpace(meta.Name)) continue;
                    rows.Add(new ToolRow
                    {
                        Name = meta.Name,
                        Description = Truncate(meta.Description),
                        Source = SettingsText.Text("settings.tools.source.baked", "Baked"),
                        TimeoutDisplay = TimeoutDisplay(bakedTimeout),
                        TimeoutHelpText = TimeoutHelp(bakedTimeout),
                    });
                }
                snapshot.BakedCount = rows.Count - snapshot.BuiltInCount;
            }

            snapshot.Rows = rows;
        }

        public static string TimeoutDisplay(RvtMcp.ToolCatalog.TimeoutPolicy timeout)
        {
            if (timeout == null) return "-";
            if (timeout.PolicyKind == "fixed" && timeout.DefaultSeconds.HasValue)
                return timeout.DefaultSeconds.Value + " s";
            if (timeout.PolicyKind == "long_run_parameter" && timeout.ParameterDefaultSeconds.HasValue
                && timeout.ParameterMinimumSeconds.HasValue && timeout.ParameterMaximumSeconds.HasValue)
                return timeout.ParameterDefaultSeconds.Value + " s (" + timeout.ParameterMinimumSeconds.Value + "-" + timeout.ParameterMaximumSeconds.Value + " s)";
            return "-";
        }

        public static string TimeoutHelp(RvtMcp.ToolCatalog.TimeoutPolicy timeout)
        {
            if (timeout == null) return SettingsText.Text("settings.tools.timeout.unavailable", "Timeout policy is unavailable.");
            if (timeout.PolicyKind == "server_local") return SettingsText.Text("settings.tools.timeout.serverLocal", "Runs in the server; does not go through Revit.");
            var budget = TimeoutDisplay(timeout);
            var value = L.T("settings.tools.timeout.budget", ("budget", budget), ("grace", timeout.TransportGraceSeconds));
            return string.Equals(value, "settings.tools.timeout.budget", StringComparison.Ordinal)
                ? "Server budget " + budget + "; transport grace " + timeout.TransportGraceSeconds + " s."
                : value;
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return CatalogCodec.TruncateScalars(text, 160);
        }



        private void OnChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            Action publish = () => { if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); };
            try { if (_dispatcher.CheckAccess()) publish(); else _dispatcher.BeginInvoke(publish); } catch { }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CatalogStore.Changed -= OnChanged;
            L.Changed -= OnChanged;
        }
    }
}
