using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Presentation.Settings;
using System.Windows.Input;
using CatalogStatus = RvtMcp.ToolCatalog.ToolCatalogStatus;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>Read-only projection of the current server catalog plus baked metadata.</summary>
    public sealed class ToolsViewModel : ObservableObject, ISettingsToolsPresentation
    {
        private readonly IToolsCatalogSource _source;
        private string _selectedName;
        private bool _rebuilding;
        private readonly List<ToolRow> _allRows = new List<ToolRow>();
        private bool _disposed;
        private string _sortColumn = "Name";
        private bool _sortAscending = true;
        private string _searchText = string.Empty;
        private CatalogStatus _status;
        private string _statusText;
        private string _statusHelpText;
        private int _builtInCount;
        private int _cataloguedBuiltInCount;
        private int _bakedCount;

        public ToolsViewModel(IToolsCatalogSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            Rows = new ObservableCollection<ToolRow>();
            RefreshCommand = new RelayCommand(Refresh, () => !_disposed);
            try { Refresh(); }
            catch { _source.Dispose(); throw; }
            _source.Changed += OnSourceChanged;
        }

        public ICommand RefreshCommand { get; }
        public ToolRow SelectedTool
        {
            get => Rows.FirstOrDefault(row => row.Name == _selectedName);
            set { if (!_rebuilding || value != null) SetProperty(ref _selectedName, value?.Name, nameof(SelectedTool)); }
        }
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
        public string SearchText
        {
            get => _searchText;
            set
            {
                var text = value ?? string.Empty;
                if (string.Equals(_searchText, text, StringComparison.Ordinal)) return;
                _searchText = text;
                if (_disposed) return;
                ApplySort();
                OnPropertyChanged(nameof(SearchText));
            }
        }

        public void Refresh()
        {
            if (_disposed) return;
            var snapshot = _source.Read();
            _status = snapshot.Status;
            _allRows.Clear();
            _allRows.AddRange(snapshot.Rows);
            _builtInCount = snapshot.BuiltInCount;
            _cataloguedBuiltInCount = snapshot.CataloguedBuiltInCount;
            _bakedCount = snapshot.BakedCount;
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
                    _statusHelpText = snapshot.Error;
                    break;
                case CatalogStatus.Current:
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
            var visible = _allRows.Where(row => row.MatchesQuery(_searchText));
            var ordered = _sortAscending
                ? visible.OrderBy(key, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal)
                : visible.OrderByDescending(key, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal);
            _rebuilding = true;
            Rows.Clear();
            var no = 0;
            foreach (var row in ordered)
            {
                row.No = ++no;
                Rows.Add(row);
            }
            _rebuilding = false;
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(SelectedTool));
            ApplyFilterStatus();
        }

        private void ApplyFilterStatus()
        {
            if (_status != CatalogStatus.Current) return;
            if (_allRows.Count > 0 && Rows.Count == 0 && !string.IsNullOrWhiteSpace(_searchText))
                _statusText = SettingsText.Text("settings.tools.status.noMatch", "No tools match this search.");
            else if (_allRows.Count == 0)
                _statusText = SettingsText.Text("settings.tools.status.empty", "No tools are exposed by the connected server.");
            else
                _statusText = string.Empty;
            OnPropertyChanged(nameof(StatusText));
        }

        private void OnSourceChanged(object sender, EventArgs e) => Refresh();

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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _source.Changed -= OnSourceChanged;
            _source.Dispose();
            ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
        }
    }
}
