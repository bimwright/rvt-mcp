using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin.Presentation.History
{
    public sealed class ActivityHistoryViewModel : StatusViewModel
    {
        private readonly IHistoryRuntime _runtime;
        private readonly IHistoryDialogs _dialogs;
        private readonly Dictionary<string, string> _journalBodies = new Dictionary<string, string>(StringComparer.Ordinal);
        private McpCallEntry _selectedEntry;
        private string _search = "", _filter = "all", _kind = "all";
        private int? _pastLoaded;
        private int _languageVersion;
        private bool _disposed;

        public ActivityHistoryViewModel(IHistoryRuntime runtime, IHistoryDialogs dialogs)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _languageVersion = runtime.LanguageVersion;
            LoadPastCommand = new RelayCommand(LoadPast, () => !_disposed && !_pastLoaded.HasValue);
            NewSessionCommand = new RelayCommand(NewSession, () => !_disposed);
            OpenLogsCommand = new RelayCommand(runtime.OpenLogs, () => !_disposed);
            RerunCommand = new AsyncRelayCommand(Rerun, () => !_disposed && CanRerun(SelectedEntry), OnRerunError);
            RerunCommand.PropertyChanged += OnRunningChanged;
            runtime.LanguageChanged += OnLanguageChanged;
        }

        public ObservableCollection<McpCallEntry> Entries => _runtime.Entries;
        public RelayCommand LoadPastCommand { get; }
        public RelayCommand NewSessionCommand { get; }
        public RelayCommand OpenLogsCommand { get; }
        public AsyncRelayCommand RerunCommand { get; }
        public string SearchText { get => _search; set => SetProperty(ref _search, value ?? ""); }
        public string ResultFilter { get => _filter; set { if (value != null) SetProperty(ref _filter, value); } }
        public string KindFilter { get => _kind; set { if (value != null) SetProperty(ref _kind, value); } }
        public int LanguageVersion => _languageVersion;
        public string LoadPastLabel => !_pastLoaded.HasValue ? L.T("history.toolbar.loadPast")
            : _pastLoaded == 0 ? L.T("history.toolbar.noPastEntries") : L.T("history.toolbar.loadedPast", ("count", _pastLoaded.Value));
        public string RerunLabel => L.T(RerunCommand.IsRunning ? "history.rerun.running" : "history.rerun.button");
        public HistoryRerunOutput RerunOutput { get; private set; }
        public string RerunError { get; private set; }
        public McpCallEntry SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (!SetProperty(ref _selectedEntry, value)) return;
                RerunOutput = null;
                RerunError = null;
                UpdateSelectionStatus();
                RerunCommand.RaiseCanExecuteChanged();
            }
        }

        public void Activate() { if (!_disposed) { _runtime.Activate(); OnLanguageChanged(null, EventArgs.Empty); } }
        public void Deactivate() => _runtime.Deactivate();
        public string Description(string toolName) => _runtime.Description(toolName);
        public bool Matches(McpCallEntry entry)
        {
            if (entry == null) return false;
            if (SearchText.Length > 0 && !Contains(entry.ToolName, SearchText) && !Contains(entry.Summary, SearchText)
                && !Contains(entry.ParamsJson, SearchText) && !Contains(entry.ErrorMessage, SearchText)) return false;
            if (ResultFilter == "success" && !entry.Success || ResultFilter == "failed" && entry.Success) return false;
            var kind = ToolActivityClassifier.Classify(entry.ToolName);
            return !(KindFilter == "read" && kind != ToolActivityKind.Read || KindFilter == "write" && kind != ToolActivityKind.Write);
        }
        private static bool Contains(string value, string term) => value != null && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        public bool CanRerun(McpCallEntry entry) => entry != null && !entry.IsHistorical && !entry.ParamsTruncated
            && (entry.ToolName != "send_code_to_revit" || !string.IsNullOrEmpty(entry.CodeSnippet) || ResolveCode(entry) != null);
        private string ResolveCode(McpCallEntry entry)
        {
            try
            {
                var hash = JObject.Parse(entry.ParamsJson)?.Value<string>("code_hash");
                if (string.IsNullOrEmpty(hash)) return null;
                if (!_journalBodies.TryGetValue(hash, out var code)) _journalBodies[hash] = code = _runtime.FindCode(hash);
                return code;
            }
            catch { return null; }
        }

        private void LoadPast()
        {
            _pastLoaded = _runtime.LoadPastSessions();
            OnPropertyChanged(nameof(LoadPastLabel));
            LoadPastCommand.RaiseCanExecuteChanged();
        }
        private void NewSession()
        {
            if (!_dialogs.ConfirmNewSession()) return;
            _runtime.ClearSession();
            SelectedEntry = null;
        }
        private async Task Rerun()
        {
            var entry = SelectedEntry;
            var request = new HistoryRerunRequest { ToolName = entry.ToolName, ParamsJson = entry.ParamsJson,
                OriginalIndex = entry.Index, OriginalResultJson = entry.ResultJson };
            if (entry.ToolName == "send_code_to_revit")
            {
                var redacted = string.IsNullOrEmpty(entry.CodeSnippet);
                if (redacted)
                {
                    var body = ResolveCode(entry);
                    if (body == null) return;
                    request.ParamsJson = new JObject { ["code"] = body }.ToString(Newtonsoft.Json.Formatting.None);
                }
                if (!_dialogs.ConfirmRerun(redacted)) return;
            }
            var output = await _runtime.Rerun(request);
            if (_disposed) return;
            RerunOutput = output;
            RerunError = null;
            OnPropertyChanged(nameof(RerunOutput));
            SetStatus($"{output.Entry.DurationMs}ms · {(output.Entry.Success ? L.T("history.footer.ok") : L.T("history.footer.fail"))}{L.T("history.footer.rerunOf", ("index", output.OriginalIndex))}",
                output.Entry.Success ? UiTone.Neutral : UiTone.Error);
        }
        private void OnRerunError(Exception error)
        {
            if (_disposed) return;
            RerunError = L.T("history.rerun.failed", ("message", error.Message));
            OnPropertyChanged(nameof(RerunError));
        }
        private void OnRunningChanged(object sender, PropertyChangedEventArgs e) { if (!_disposed) OnPropertyChanged(nameof(RerunLabel)); }
        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_disposed || _languageVersion == _runtime.LanguageVersion) return;
            _languageVersion = _runtime.LanguageVersion;
            _runtime.RefreshSummaries();
            OnPropertyChanged(nameof(LanguageVersion));
            OnPropertyChanged(nameof(LoadPastLabel));
            OnPropertyChanged(nameof(RerunLabel));
            if (RerunOutput == null && !RerunCommand.IsRunning) UpdateSelectionStatus();
        }
        private void UpdateSelectionStatus()
        {
            var entry = SelectedEntry;
            if (entry == null) { SetStatus(""); return; }
            var rerun = entry.RerunOfIndex.HasValue ? L.T("history.footer.rerunOf", ("index", entry.RerunOfIndex.Value)) : "";
            var session = entry.IsHistorical ? L.T("history.footer.session", ("tag", entry.SessionTag)) : "";
            SetStatus($"{entry.DurationMs}ms · {(entry.Success ? L.T("history.footer.ok") : L.T("history.footer.fail"))} · {entry.TimeLabel}{rerun}{session}",
                entry.Success ? UiTone.Neutral : UiTone.Error);
        }
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _runtime.LanguageChanged -= OnLanguageChanged;
            RerunCommand.PropertyChanged -= OnRunningChanged;
            _runtime.Dispose();
            _journalBodies.Clear();
            LoadPastCommand.RaiseCanExecuteChanged();
            NewSessionCommand.RaiseCanExecuteChanged();
            OpenLogsCommand.RaiseCanExecuteChanged();
            RerunCommand.RaiseCanExecuteChanged();
        }
    }
}
