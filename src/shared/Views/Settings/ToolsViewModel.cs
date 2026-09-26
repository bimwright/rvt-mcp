using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.ToolBaker;
using CatalogEntry = RvtMcp.ToolCatalog.ToolCatalogEntry;
using CatalogStatus = RvtMcp.ToolCatalog.ToolCatalogStatus;
using CatalogStore = RvtMcp.ToolCatalog.ToolCatalogStore;

namespace RvtMcp.Plugin.Views.Settings
{
    public sealed class ToolRow
    {
        public int No { get; internal set; }
        public string Name { get; internal set; }
        public string Description { get; internal set; }
        public string DescriptionDisplay
        {
            get
            {
                if (!IsSummaryFallback) return Description;
                var marker = L.T("settings.tools.fallback");
                if (string.Equals(marker, "settings.tools.fallback", StringComparison.Ordinal)) marker = "(fallback)";
                var suffix = " " + marker;
                if (ToolSummaryCatalog.ScalarLength(Description) + ToolSummaryCatalog.ScalarLength(suffix) <= 160)
                    return Description + suffix;
                var limit = Math.Max(0, 160 - ToolSummaryCatalog.ScalarLength(suffix));
                return ToolsViewModel.TakeScalars(Description, limit).TrimEnd() + suffix;
            }
        }
        public bool IsSummaryFallback { get; internal set; }
        public string Source { get; internal set; }
        public string TimeoutDisplay { get; internal set; }
        public string TimeoutHelpText { get; internal set; }
    }

    /// <summary>Read-only projection of the current server catalog plus baked metadata.</summary>
    public sealed class ToolsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly BakedToolRegistry _registry;
        private readonly Assembly _summaryAssembly;
        private readonly Dispatcher _dispatcher;
        private bool _disposed;
        private CatalogStatus _status;
        private string _statusText;
        private string _statusHelpText;
        private int _builtInCount;
        private int _cataloguedBuiltInCount;
        private int _bakedCount;

        public ToolsViewModel(BakedToolRegistry registry, Assembly summaryAssembly = null)
        {
            _registry = registry;
            _summaryAssembly = summaryAssembly ?? Assembly.GetExecutingAssembly();
            _dispatcher = Dispatcher.CurrentDispatcher;
            Rows = new ObservableCollection<ToolRow>();
            CatalogStore.Changed += OnCatalogChanged;
            L.Changed += OnLanguageChanged;
            Refresh();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<ToolRow> Rows { get; }
        public CatalogStatus Status => _status;
        public string StatusText => _statusText;
        public string StatusHelpText => _statusHelpText;
        public int BuiltInCount => _builtInCount;
        public int CataloguedBuiltInCount => _cataloguedBuiltInCount;
        public int BakedCount => _bakedCount;
        public string CountsText => L.T("settings.tools.counts",
            ("active", _builtInCount), ("catalogued", _cataloguedBuiltInCount), ("baked", _bakedCount)) == "settings.tools.counts"
            ? "Built-in: " + _builtInCount + " / " + _cataloguedBuiltInCount + " · Baked: " + _bakedCount
            : L.T("settings.tools.counts", ("active", _builtInCount), ("catalogued", _cataloguedBuiltInCount), ("baked", _bakedCount));
        public string SourceBuiltIn => Text("settings.tools.source.builtIn", "Built-in");
        public string SourceBaked => Text("settings.tools.source.baked", "Baked");

        public void Refresh()
        {
            if (!_dispatcher.CheckAccess())
            {
                try { _dispatcher.BeginInvoke(new Action(Refresh)); } catch { }
                return;
            }
            if (_disposed) return;
            _status = CatalogStore.Status;
            Rows.Clear();
            _builtInCount = 0;
            _cataloguedBuiltInCount = 0;
            _bakedCount = 0;
            switch (_status)
            {
                case CatalogStatus.NotConnected:
                    _statusText = Text("settings.tools.status.notConnected", "No MCP server connected.");
                    _statusHelpText = null;
                    break;
                case CatalogStatus.ConnectedNoCatalog:
                    _statusText = Text("settings.tools.status.noCatalog", "The connected server has not sent a tool catalog.");
                    _statusHelpText = null;
                    break;
                case CatalogStatus.Invalid:
                    _statusText = Text("settings.tools.status.invalid", "The server tool catalog was rejected.");
                    _statusHelpText = CatalogStore.Error;
                    break;
                case CatalogStatus.Current:
                    BuildRows(CatalogStore.Current);
                    _statusText = Rows.Count == 0 ? Text("settings.tools.status.empty", "No tools are exposed by the connected server.") : string.Empty;
                    _statusHelpText = null;
                    break;
            }
            NotifyAll();
        }

        private void BuildRows(RvtMcp.ToolCatalog.ToolCatalog catalog)
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
                    Source = SourceBuiltIn,
                    TimeoutDisplay = TimeoutDisplay(entry.Timeout),
                    TimeoutHelpText = TimeoutHelp(entry.Timeout),
                };
            }).ToList();
            _builtInCount = rows.Count;
            _cataloguedBuiltInCount = catalog.CataloguedBuiltInCount;

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
                        Source = SourceBaked,
                        TimeoutDisplay = TimeoutDisplay(bakedTimeout),
                        TimeoutHelpText = TimeoutHelp(bakedTimeout),
                    });
                }
                _bakedCount = rows.Count - _builtInCount;
            }

            foreach (var row in rows.OrderBy(row => row.Name ?? string.Empty, StringComparer.Ordinal))
            {
                row.No = Rows.Count + 1;
                Rows.Add(row);
            }
        }

        private void OnCatalogChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            if (_dispatcher.CheckAccess()) Refresh();
            else try { _dispatcher.BeginInvoke(new Action(Refresh)); } catch { }
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            if (_dispatcher.CheckAccess()) Refresh();
            else try { _dispatcher.BeginInvoke(new Action(Refresh)); } catch { }
        }

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusHelpText));
            OnPropertyChanged(nameof(BuiltInCount));
            OnPropertyChanged(nameof(CataloguedBuiltInCount));
            OnPropertyChanged(nameof(BakedCount));
            OnPropertyChanged(nameof(CountsText));
            OnPropertyChanged(nameof(Rows));
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CatalogStore.Changed -= OnCatalogChanged;
            L.Changed -= OnLanguageChanged;
        }

        public static string TimeoutDisplay(RvtMcp.ToolCatalog.TimeoutPolicy timeout)
        {
            if (timeout == null) return "—";
            if (timeout.PolicyKind == "fixed" && timeout.DefaultSeconds.HasValue)
                return timeout.DefaultSeconds.Value + " s";
            if (timeout.PolicyKind == "long_run_parameter" && timeout.ParameterDefaultSeconds.HasValue
                && timeout.ParameterMinimumSeconds.HasValue && timeout.ParameterMaximumSeconds.HasValue)
                return timeout.ParameterDefaultSeconds.Value + " s (" + timeout.ParameterMinimumSeconds.Value + "–" + timeout.ParameterMaximumSeconds.Value + " s)";
            return "—";
        }

        public static string TimeoutHelp(RvtMcp.ToolCatalog.TimeoutPolicy timeout)
        {
            if (timeout == null) return "Timeout policy is unavailable.";
            if (timeout.PolicyKind == "server_local") return "Runs in the server; does not go through Revit.";
            var budget = TimeoutDisplay(timeout);
            return "Server budget " + budget + "; transport grace " + timeout.TransportGraceSeconds + " s.";
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (ToolSummaryCatalog.ScalarLength(text) <= 160) return text;
            var end = 0;
            var scalars = 0;
            for (var i = 0; i < text.Length && scalars < 159; i++, scalars++)
            {
                end = i + 1;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) end = ++i + 1;
            }
            return text.Substring(0, end).TrimEnd() + "…";
        }

        private static string Text(string key, string fallback)
        {
            var value = L.T(key);
            return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
        }

        internal static string TakeScalars(string value, int count)
        {
            if (string.IsNullOrEmpty(value) || count <= 0) return string.Empty;
            var end = 0;
            var scalars = 0;
            for (var i = 0; i < value.Length && scalars < count; i++, scalars++)
            {
                end = i + 1;
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) end = ++i + 1;
            }
            return value.Substring(0, end);
        }
    }
}
