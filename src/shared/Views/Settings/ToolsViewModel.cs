using System;
using System.Collections.Generic;
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
using CatalogCodec = RvtMcp.ToolCatalog.ToolCatalogCodec;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>Read-only projection of the current server catalog plus baked metadata.</summary>
    public sealed class ToolsViewModel : ISettingsToolsPresentation
    {
        private readonly BakedToolRegistry _registry;
        private readonly Assembly _summaryAssembly;
        private readonly Dispatcher _dispatcher;
        private readonly List<ToolRow> _allRows = new List<ToolRow>();
        private bool _disposed;
        private string _sortColumn = "Name";
        private bool _sortAscending = true;
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
        public string CountsText => SettingsText.Text("settings.tools.counts",
            "Built-in: " + _builtInCount + " / " + _cataloguedBuiltInCount + " · Baked: " + _bakedCount,
            ("active", _builtInCount), ("catalogued", _cataloguedBuiltInCount), ("baked", _bakedCount));
        public string SourceBuiltIn => SettingsText.Text("settings.tools.source.builtIn", "Built-in");
        public string SourceBaked => SettingsText.Text("settings.tools.source.baked", "Baked");
        public string SortColumn => _sortColumn;
        public bool SortAscending => _sortAscending;

        public void Refresh()
        {
            if (!_dispatcher.CheckAccess())
            {
                try { _dispatcher.BeginInvoke(new Action(Refresh)); } catch { }
                return;
            }
            if (_disposed) return;
            _status = CatalogStore.Status;
            _allRows.Clear();
            Rows.Clear();
            _builtInCount = 0;
            _cataloguedBuiltInCount = 0;
            _bakedCount = 0;
            switch (_status)
            {
                case CatalogStatus.NotConnected:
                    _statusText = SettingsText.Text("settings.tools.status.notConnected", "No MCP server connected.");
                    _statusHelpText = null;
                    break;
                case CatalogStatus.ConnectedNoCatalog:
                    _statusText = SettingsText.Text("settings.tools.status.noCatalog", "The connected server has not sent a tool catalog.");
                    _statusHelpText = null;
                    break;
                case CatalogStatus.Invalid:
                    _statusText = SettingsText.Text("settings.tools.status.invalid", "The server tool catalog was rejected.");
                    _statusHelpText = CatalogStore.Error;
                    break;
                case CatalogStatus.Current:
                    BuildRows(CatalogStore.Current);
                    _statusText = _allRows.Count == 0 ? SettingsText.Text("settings.tools.status.empty", "No tools are exposed by the connected server.") : string.Empty;
                    _statusHelpText = null;
                    break;
            }
            ApplySort();
            NotifyAll();
        }

        /// <summary>Re-sorts the visible rows and renumbers No. — the grid handles Sorting and calls this.</summary>
        public void SortBy(string column)
        {
            if (string.IsNullOrWhiteSpace(column)) return;
            if (string.Equals(_sortColumn, column, StringComparison.Ordinal))
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = column;
                _sortAscending = true;
            }
            ApplySort();
        }

        private void ApplySort()
        {
            Func<ToolRow, string> key;
            switch (_sortColumn)
            {
                case "Description":
                    key = row => row.DescriptionDisplay;
                    break;
                case "Source":
                    key = row => row.Source;
                    break;
                case "Timeout":
                    key = row => row.TimeoutDisplay;
                    break;
                default:
                    // "No" and "Name" share the tool-name order; No. is a display index, not an id.
                    key = row => row.Name;
                    break;
            }
            var ordered = _sortAscending
                ? _allRows.OrderBy(key, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal)
                : _allRows.OrderByDescending(key, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal);
            Rows.Clear();
            var no = 0;
            foreach (var row in ordered)
            {
                row.No = ++no;
                Rows.Add(row);
            }
            OnPropertyChanged(nameof(Rows));
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

            _allRows.AddRange(rows);
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


    }
}
