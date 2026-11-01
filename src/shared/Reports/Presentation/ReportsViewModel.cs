using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public enum ReportFilter { ActiveModel, All }
    public enum DisplayMode { Wide, Compact }
    public enum ReportStatusTone { Neutral, Success, Warning, Error }

    public sealed class ReportsViewModel : ObservableObject, IDisposable
    {
        private readonly IReportsRuntime _runtime;
        private readonly INavigationService _navigation;
        private readonly object _gate = new object();
        private readonly List<ReportListItemViewModel> _all = new List<ReportListItemViewModel>();
        private bool _loading, _pending, _rebuildingVisible;
        private volatile bool _disposed;
        private long _generation;
        private Task _refreshTask;
        private string _pendingReportId;
        private ReportFilter _filter = ReportFilter.ActiveModel;
        private DisplayMode _mode;
        private ReportListItemViewModel _selectedReport;
        private ReportDetailViewModel _selectedDetail;
        private bool _deleteConfirmation, _compactChangesExpanded;
        private string _statusText = "";
        private ReportStatusTone _statusTone;

        public ReportsViewModel(IReportsRuntime runtime, INavigationService navigation, DisplayMode initialMode)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
            _mode = initialMode;
            Reports = new ObservableCollection<ReportListItemViewModel>();
            DeleteCommand = new RelayCommand(Delete, () => !_disposed && SelectedReport != null);
            CopyCommand = new RelayCommand(Copy, () => !_disposed && SelectedDetail != null);
            runtime.ReportsChanged += OnReportsChanged;
            runtime.ActiveDocumentChanged += OnActiveDocumentChanged;
            navigation.Completed += OnNavigationCompleted;
        }

        public ObservableCollection<ReportListItemViewModel> Reports { get; }
        public RelayCommand DeleteCommand { get; }
        public RelayCommand CopyCommand { get; }
        public ReportFilter Filter
        {
            get => _filter;
            set
            {
                if (!SetProperty(ref _filter, value)) return;
                CancelDelete();
                RebuildVisible();
            }
        }
        public DisplayMode Mode
        {
            get => _mode;
            set
            {
                if (!SetProperty(ref _mode, value)) return;
                CancelDelete();
                if (_mode == DisplayMode.Wide && SelectedReport == null && Reports.Count > 0)
                    SetSelection(Reports[0]);
            }
        }
        public ReportListItemViewModel SelectedReport
        {
            get => _selectedReport;
            set
            {
                if (_rebuildingVisible) return;
                if (value != null && !Reports.Contains(value)) return;
                if (value == null && Mode == DisplayMode.Wide && Reports.Count > 0) value = Reports[0];
                SetSelection(value);
            }
        }
        public ReportDetailViewModel SelectedDetail => _selectedDetail;
        public bool IsDeleteConfirmationPending => _deleteConfirmation;
        public string DeleteLabel => L.T(_deleteConfirmation ? "reports.delete.confirm" : "reports.delete.button");
        public bool CompactChangesExpanded
        {
            get => _compactChangesExpanded;
            set => SetProperty(ref _compactChangesExpanded, value);
        }
        public string StatusText => _statusText;
        public ReportStatusTone StatusTone => _statusTone;
        public string EmptyText => Filter == ReportFilter.ActiveModel && _runtime.ActiveDocument == null
            ? L.T("reports.empty.noActiveDocument")
            : _all.Count == 0 ? L.T("reports.empty.noReports")
            : Filter == ReportFilter.ActiveModel ? L.T("reports.empty.noModelReports") : L.T("reports.empty.noReports");

        public Task RefreshAsync()
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;
                _generation++;
                if (_loading)
                {
                    _pending = true;
                    return _refreshTask;
                }
                _loading = true;
                _refreshTask = Task.Run(RefreshLoopAsync);
                return _refreshTask;
            }
        }

        private async Task RefreshLoopAsync()
        {
            while (true)
            {
                long generation;
                lock (_gate) { generation = _generation; _pending = false; }
                IReadOnlyList<AgentReport> loaded = null;
                Exception error = null;
                try { loaded = _runtime.LoadReports(); }
                catch (Exception ex) { error = ex; }
                try
                {
                    await PostAsync(() =>
                    {
                        lock (_gate)
                        {
                            if (_disposed || generation != _generation) return;
                            if (error != null) { SetStatus(L.T("reports.status.loadFailed"), ReportStatusTone.Error); return; }
                            // An unchanged report keeps its view models, so a reload does not reset what the user is reading.
                            var previous = new Dictionary<string, ReportListItemViewModel>(StringComparer.Ordinal);
                            foreach (var item in _all)
                                if (item.ReportId != null && !previous.ContainsKey(item.ReportId)) previous.Add(item.ReportId, item);
                            _all.Clear();
                            foreach (var report in loaded ?? new AgentReport[0])
                            {
                                if (report == null) continue;
                                if (report.ReportId != null && previous.TryGetValue(report.ReportId, out var kept)
                                    && string.Equals(kept.Report.UpdatedUtc, report.UpdatedUtc, StringComparison.Ordinal))
                                {
                                    previous.Remove(report.ReportId);
                                    _all.Add(kept);
                                }
                                else _all.Add(new ReportListItemViewModel(report, () => _runtime.ActiveDocument, () => _runtime.Now));
                            }
                            RebuildVisible();
                        }
                    }).ConfigureAwait(false);
                }
                catch
                {
                    lock (_gate) { _loading = false; }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed && _pending) continue;
                    _loading = false;
                    return;
                }
            }
        }

        private Task PostAsync(Action action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runtime.Post(() =>
            {
                try { action(); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return completion.Task;
        }

        public void SelectReport(ReportListItemViewModel report)
        {
            if (Mode == DisplayMode.Compact && report != null && ReferenceEquals(report, SelectedReport)) SelectedReport = null;
            else SelectedReport = report;
        }

        public void ShowReport(string reportId)
        {
            if (_disposed || string.IsNullOrEmpty(reportId)) return;
            _pendingReportId = reportId;
            if (_all.Any(r => string.Equals(r.ReportId, reportId, StringComparison.Ordinal))) RebuildVisible();
            else _ = RefreshAsync();
        }

        private void RebuildVisible()
        {
            var desiredId = _selectedReport?.ReportId;
            if (_pendingReportId != null)
            {
                var target = _all.FirstOrDefault(r => string.Equals(r.ReportId, _pendingReportId, StringComparison.Ordinal));
                if (target != null)
                {
                    if (_filter == ReportFilter.ActiveModel && target.IsOtherModel)
                    {
                        _filter = ReportFilter.All;
                        OnPropertyChanged(nameof(Filter));
                        CancelDelete();
                    }
                    desiredId = target.ReportId;
                    _pendingReportId = null;
                }
            }
            var visible = _filter == ReportFilter.All ? _all : _all.Where(r => !r.IsOtherModel).ToList();
            _rebuildingVisible = true;
            try
            {
                // Insert and move in place instead of resetting, so a bound list keeps its scroll position.
                for (var i = 0; i < visible.Count; i++)
                {
                    var index = Reports.IndexOf(visible[i]);
                    if (index == i) continue;
                    if (index < 0) Reports.Insert(i, visible[i]);
                    else Reports.Move(index, i);
                }
                while (Reports.Count > visible.Count) Reports.RemoveAt(Reports.Count - 1);
            }
            finally { _rebuildingVisible = false; }
            var selected = Reports.FirstOrDefault(r => string.Equals(r.ReportId, desiredId, StringComparison.Ordinal));
            if (selected == null && Mode == DisplayMode.Wide && Reports.Count > 0) selected = Reports[0];
            var retainedSelection = selected != null && ReferenceEquals(_selectedReport, selected);
            SetSelection(selected);
            if (retainedSelection) OnPropertyChanged(nameof(SelectedReport));
            OnPropertyChanged(nameof(EmptyText));
        }

        private void SetSelection(ReportListItemViewModel selected)
        {
            var changedId = !string.Equals(_selectedReport?.ReportId, selected?.ReportId, StringComparison.Ordinal);
            if (ReferenceEquals(_selectedReport, selected)) return;
            _selectedReport = selected;
            _selectedDetail = selected == null ? null : new ReportDetailViewModel(selected.Report, _navigation,
                () => _runtime.ActiveDocument, () => _runtime.Now);
            OnPropertyChanged(nameof(SelectedReport));
            OnPropertyChanged(nameof(SelectedDetail));
            if (changedId)
            {
                CancelDelete();
                CompactChangesExpanded = false;
                SetStatus("", ReportStatusTone.Neutral);
            }
            DeleteCommand.RaiseCanExecuteChanged();
            CopyCommand.RaiseCanExecuteChanged();
        }

        private void Delete()
        {
            if (!_deleteConfirmation)
            {
                _deleteConfirmation = true;
                OnPropertyChanged(nameof(IsDeleteConfirmationPending));
                OnPropertyChanged(nameof(DeleteLabel));
                return;
            }
            var reportId = SelectedReport.ReportId;
            CancelDelete();
            try
            {
                _runtime.DeleteReport(reportId);
                lock (_gate)
                {
                    if (_loading) { _generation++; _pending = true; }
                }
                _all.RemoveAll(r => string.Equals(r.ReportId, reportId, StringComparison.Ordinal));
                RebuildVisible();
                SetStatus(L.T("reports.status.deleted"), ReportStatusTone.Success);
            }
            catch (Exception)
            {
                SetStatus(L.T("reports.status.deleteFailed"), ReportStatusTone.Error);
            }
        }

        private void CancelDelete()
        {
            if (!_deleteConfirmation) return;
            _deleteConfirmation = false;
            OnPropertyChanged(nameof(IsDeleteConfirmationPending));
            OnPropertyChanged(nameof(DeleteLabel));
        }

        private void Copy()
        {
            var plain = SelectedDetail.Body.ToPlainText();
            var text = SelectedDetail.Summary ?? "";
            if (!string.IsNullOrEmpty(plain)) text += "\n\n" + plain;
            var copied = _runtime.CopyText(text);
            SetStatus(copied ? L.T("reports.status.copied") : L.T("reports.status.copyFailed"),
                copied ? ReportStatusTone.Success : ReportStatusTone.Error);
        }

        private void OnReportsChanged(object sender, EventArgs e) { if (!_disposed) _ = RefreshAsync(); }
        private void OnActiveDocumentChanged(object sender, EventArgs e)
        {
            _runtime.Post(() =>
            {
                if (_disposed) return;
                foreach (var item in _all) item.UpdateActiveDocument();
                _selectedDetail?.UpdateActiveDocument();
                RebuildVisible();
            });
        }
        private void OnNavigationCompleted(object sender, ReportNavigationResult result)
        {
            _runtime.Post(() =>
            {
                if (_disposed || result == null || result.Outcome == ReportNavigationOutcome.NotMatched) return;
                switch (result.Outcome)
                {
                    case ReportNavigationOutcome.Selected:
                        if (result.MissingCount > 0)
                            SetStatus(L.T("reports.navigation.selectedSome", ("selected", result.SelectedCount), ("missing", result.MissingCount),
                                ("total", result.SelectedCount + result.MissingCount), ("view", result.ViewName ?? "")), ReportStatusTone.Warning);
                        else SetStatus(L.T("reports.navigation.selected", ("selected", result.SelectedCount), ("view", result.ViewName ?? "")), ReportStatusTone.Success);
                        break;
                    case ReportNavigationOutcome.ViewActivated:
                        SetStatus(L.T("reports.navigation.viewActivated", ("view", result.ViewName ?? "")), ReportStatusTone.Success); break;
                    case ReportNavigationOutcome.ElementsGone: SetStatus(L.T("reports.navigation.elementsGone"), ReportStatusTone.Warning); break;
                    case ReportNavigationOutcome.NoGraphics: SetStatus(L.T("reports.navigation.noGraphics"), ReportStatusTone.Warning); break;
                    case ReportNavigationOutcome.NoViewFound: SetStatus(L.T("reports.navigation.noViewFound"), ReportStatusTone.Warning); break;
                    case ReportNavigationOutcome.Failed: SetStatus(L.T("reports.navigation.failed"), ReportStatusTone.Error); break;
                }
            });
        }
        private void SetStatus(string text, ReportStatusTone tone)
        {
            SetProperty(ref _statusText, text, nameof(StatusText));
            SetProperty(ref _statusTone, tone, nameof(StatusTone));
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _generation++;
                _pending = false;
            }
            _runtime.ReportsChanged -= OnReportsChanged;
            _runtime.ActiveDocumentChanged -= OnActiveDocumentChanged;
            _navigation.Completed -= OnNavigationCompleted;
            DeleteCommand.RaiseCanExecuteChanged();
            CopyCommand.RaiseCanExecuteChanged();
        }
    }
}
