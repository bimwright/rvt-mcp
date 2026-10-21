using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using RvtMcp.Failures;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Presentation.Issues
{
    internal sealed partial class IssuesViewModel : StatusViewModel
    {
        private const int MaxIssueRows = 500;
        private readonly IIssuesRuntime _runtime;
        private readonly IIssuesDialogs _dialogs;
        private readonly Dictionary<string, RelayCommand> _decisions = new Dictionary<string, RelayCommand>();
        private ModelRow _selectedModel;
        private IssueRow _selectedIssue;
        private RuleRow _selectedRule;
        private FailureIncident _incident;
        private FailureOperation _operation;
        private string _state = "open", _search = "", _reason = "", _lastScope;
        private bool _verified, _consent, _loading, _disposed, _active;
        private UiTone _bannerTone;
        private int _languageVersion;
        private bool _canRework, _canAccept, _canRule, _canNative, _canRevoke;

        internal IssuesViewModel(IIssuesRuntime runtime, IIssuesDialogs dialogs)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            RefreshCommand = new RelayCommand(Refresh, () => !_disposed);
            ResetCommand = new RelayCommand(ResetLedger, () => !_disposed);
            RevokeCommand = new RelayCommand(RevokeRule, () => !_disposed && _canRevoke);
            CopyCommand = new RelayCommand(CopyDetails, () => !_disposed && _incident != null);
            foreach (var action in new[] { "rework", "abandon", "accept_once", "create_rule", "native_grant" })
            {
                var captured = action;
                _decisions[action] = new RelayCommand(() => RecordDecision(captured), () => !_disposed && Allowed(captured));
            }
            runtime.ContextChanged += OnContextChanged;
            runtime.LanguageChanged += OnLanguageChanged;
        }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand ResetCommand { get; }
        public RelayCommand RevokeCommand { get; }
        public RelayCommand CopyCommand { get; }
        public ICommand DecisionCommand(string action) => _decisions[action];
        public IReadOnlyList<ModelRow> Models { get; private set; } = new ModelRow[0];
        public IReadOnlyList<IssueRow> Rows { get; private set; } = new IssueRow[0];
        public IReadOnlyList<RuleRow> Rules { get; private set; } = new RuleRow[0];
        public FailureIncident Incident => _incident;
        public FailureOperation Operation => _operation;
        public string PageStatus { get; private set; } = "";
        public string DecisionHint => _incident == null ? L.T("failure.detail.prompt") : "";
        public bool HasDialog => FailureIssuesPresentation.DialogAdapter(_incident) != null;
        public bool IsAcceptableWarning => _incident?.State == "open" && FailureIssuesPresentation.CanAcceptWarning(_incident);
        public bool HasNativeAction => !string.IsNullOrWhiteSpace(_incident?.NativeAction);
        public bool SearchEmpty => !string.IsNullOrWhiteSpace(SearchText) && !Models.Any(row => row.MatchesSearch);
        public int ActionStateVersion { get; private set; }
        public int LanguageVersion => _languageVersion;
        public string Reason { get => _reason; set => SetProperty(ref _reason, value ?? ""); }
        public bool VerifiedModel { get => _verified; set { if (SetProperty(ref _verified, value)) RefreshActions(); } }
        public bool DialogConsent { get => _consent; set => SetProperty(ref _consent, value); }
        public string State { get => _state; set { if (value != null && SetProperty(ref _state, value) && !_loading) Refresh(); } }
        public string SearchText { get => _search; set { if (SetProperty(ref _search, value ?? "")) ApplyOpenEmphasis(); } }
        public ModelRow SelectedModel
        {
            get => _selectedModel;
            set { if (!_loading && SetProperty(ref _selectedModel, value)) LoadPage(); }
        }
        public IssueRow SelectedIssue
        {
            get => _selectedIssue;
            set { if (!_loading && SetProperty(ref _selectedIssue, value)) SelectIssue(value?.Incident); }
        }
        public RuleRow SelectedRule
        {
            get => _selectedRule;
            set { if (!_loading && SetProperty(ref _selectedRule, value)) RefreshActions(); }
        }
        public void Activate()
        {
            if (_active || _disposed) return;
            _active = true;
            _runtime.Activate();
            Refresh();
        }
        public void Deactivate() { if (_active) { _active = false; _runtime.Deactivate(); } }
        private void OnContextChanged(object sender, EventArgs e) { if (_active && !_disposed) { ApplyOpenEmphasis(); RefreshActions(); } }
        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (!_active || _disposed) return;
            _languageVersion++;
            OnPropertyChanged(nameof(LanguageVersion));
            RefreshActions();
        }
        public void Refresh()
        {
            if (_disposed) return;
            _loading = true;
            try
            {
                var context = _runtime.Context();
                var open = _runtime.OpenScopes();
                var kept = _selectedModel;
                var models = _runtime.Models(string.IsNullOrEmpty(State) ? null : State).Select(m => new ModelRow(m, context, open)).ToList();
                if (kept != null && !models.Any(m => SameModel(m, kept)))
                {
                    var retained = _runtime.Models(null).FirstOrDefault(m => m.ModelScopeKey == kept.ModelScopeKey && m.RevitYear == kept.RevitYear);
                    if (retained != null) { retained.IssueCount = 0; models.Add(new ModelRow(retained, context, open)); }
                }
                foreach (var model in models) model.UpdateSearch(SearchText);
                Models = OrderModels(models);
                OnPropertyChanged(nameof(Models));
                _selectedModel = kept == null ? null : Models.FirstOrDefault(m => SameModel(m, kept));
                if (_selectedModel == null)
                {
                    var preferred = FailureIssuesPresentation.PreferredIssueModel(Models.Select(m => m.Source).ToList(), context.IsAvailable ? context.ModelScopeKey : null, context.RevitYear);
                    _selectedModel = Models.FirstOrDefault(m => m.Source == preferred) ?? Models.FirstOrDefault();
                }
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(SearchEmpty));
            }
            catch (Exception ex) { Models = new ModelRow[0]; _selectedModel = null; OnPropertyChanged(nameof(Models)); OnPropertyChanged(nameof(SelectedModel)); ClearRows(); SelectIssue(null); ShowFailure(ex.Message); return; }
            finally { _loading = false; }
            LoadPage();
        }
        public void ApplyOpenEmphasis()
        {
            if (_disposed) return;
            var context = _runtime.Context(); var open = _runtime.OpenScopes();
            foreach (var row in Models) { row.UpdateContext(context, open); row.UpdateSearch(SearchText); }
            var ordered = OrderModels(Models);
            if (!ordered.SequenceEqual(Models))
            {
                _loading = true;
                try { Models = ordered; OnPropertyChanged(nameof(Models)); OnPropertyChanged(nameof(SelectedModel)); }
                finally { _loading = false; }
            }
            OnPropertyChanged(nameof(SearchEmpty));
        }
        private static bool SameModel(ModelRow row, ModelRow kept) => row.ModelScopeKey == kept.ModelScopeKey && row.RevitYear == kept.RevitYear;
        private static List<ModelRow> OrderModels(IEnumerable<ModelRow> rows) => rows.OrderByDescending(row => row.SearchScore)
            .ThenByDescending(row => row.IsOpen).ThenByDescending(row => row.IsCurrent).ThenByDescending(row => row.RevitYear)
            .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.ModelScopeKey, StringComparer.Ordinal).ToList();

        private void LoadPage()
        {
            if (_loading || _disposed) return;
            var model = SelectedModel;
            if (model == null) { ClearRows(); SetBanner(L.T("failure.empty")); SelectIssue(null); return; }
            var key = model.ModelScopeKey + ":" + model.RevitYear;
            if (_lastScope != key) { Reason = ""; VerifiedModel = false; _lastScope = key; }
            try
            {
                var incidents = new List<FailureIncident>();
                string cursor = null;
                var available = false; var complete = true; var more = false;
                while (incidents.Count < MaxIssueRows)
                {
                    var page = _runtime.Query(new FailureRecordQuery { ModelScopeKey = model.ModelScopeKey, RevitYear = model.RevitYear,
                        State = string.IsNullOrEmpty(State) ? null : State, Cursor = cursor, Limit = 100, IncludeDetails = false, NewestFirst = true });
                    available = page.Available;
                    if (!available) break;
                    complete &= page.Complete;
                    incidents.AddRange(page.Records);
                    cursor = page.NextCursor; more = cursor != null;
                    if (!more) break;
                }
                var operations = new Dictionary<string, FailureOperation>();
                var rows = new List<IssueRow>();
                foreach (var incident in incidents)
                {
                    FailureOperation operation = null;
                    if (incident.OperationId != null && !operations.TryGetValue(incident.OperationId, out operation))
                        operations[incident.OperationId] = operation = _runtime.Operation(incident.OperationId);
                    rows.Add(new IssueRow(incident, operation) { Number = rows.Count + 1 });
                }
                var selectedId = _incident?.IncidentId;
                _loading = true;
                try
                {
                    Rules = _runtime.Rules(model.ModelScopeKey).Select(rule => new RuleRow(rule)).ToList();
                    _selectedRule = null;
                    OnPropertyChanged(nameof(Rules)); OnPropertyChanged(nameof(SelectedRule));
                    Rows = rows;
                    OnPropertyChanged(nameof(Rows));
                    SelectIssue(null);
                    _selectedIssue = rows.FirstOrDefault(row => row.Incident.IncidentId == selectedId) ?? rows.FirstOrDefault();
                    OnPropertyChanged(nameof(SelectedIssue));
                    SelectIssue(_selectedIssue?.Incident);
                }
                finally { _loading = false; }
                var gate = _runtime.Gate(model.ModelScopeKey);
                var parts = new List<string> { available ? L.T(gate.Blocked ? "failure.gate.blocked" : "failure.gate.clear") : L.T("failure.unavailable") };
                if (!complete) parts.Add(L.T("failure.detail.incomplete"));
                if (more) parts.Add(L.T("failure.list.newest", ("count", rows.Count)));
                if (rows.Count == 0 && available) parts.Add(L.T("failure.empty"));
                SetBanner(string.Join(" · ", parts), gate.Blocked || !available ? UiTone.Error : UiTone.Neutral);
                RefreshActions();
            }
            catch (Exception ex) { ClearRows(); SelectIssue(null); ShowFailure(ex.Message); }
        }
        private void ClearRows()
        {
            Rows = new IssueRow[0]; Rules = new RuleRow[0]; _selectedIssue = null; _selectedRule = null;
            OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(Rules));
            OnPropertyChanged(nameof(SelectedIssue)); OnPropertyChanged(nameof(SelectedRule));
        }
        private void SelectIssue(FailureIncident selected)
        {
            var same = selected != null && selected.IncidentId == _incident?.IncidentId;
            if (selected != null && _incident != null && !same) Reason = "";
            _incident = null; _operation = null;
            if (!same) { VerifiedModel = false; DialogConsent = false; }
            if (selected != null)
            {
                try
                {
                    _incident = _runtime.Query(new FailureRecordQuery { ModelScopeKey = selected.ModelScopeKey,
                        IncidentId = selected.IncidentId, IncludeDetails = true, Limit = 1 }).Records.FirstOrDefault();
                    _operation = _runtime.Operation(selected.OperationId);
                }
                catch (Exception ex) { ShowFailure(ex.Message); }
            }
            OnPropertyChanged(nameof(Incident)); OnPropertyChanged(nameof(Operation));
            RefreshActions();
        }
        public void RefreshActions()
        {
            if (_disposed) return;
            var context = _runtime.Context();
            var settled = _operation == null || _operation.Execution == "completed" || _operation.Execution == "interrupted_unknown";
            var active = settled && _incident?.State == "open";
            _canRework = active && VerifiedModel;
            var warning = active && FailureIssuesPresentation.CanAcceptWarning(_incident);
            var dialog = FailureIssuesPresentation.DialogAdapter(_incident);
            _canAccept = _canRule = VerifiedModel && (warning || active && dialog != null);
            if (settled && _incident?.State == "observed" && dialog != null) _canRule = VerifiedModel;
            var nativeContext = _incident == null ? null : _runtime.Context(_incident.ModelScopeKey);
            var revision = nativeContext?.IsAvailable == true && nativeContext.ModelScopeKey == _incident?.ModelScopeKey
                && nativeContext.RevitYear == _incident.RevitYear ? nativeContext.DocumentRevision : _incident?.DocumentRevision;
            _canNative = VerifiedModel && active && _incident.Complete && !string.IsNullOrWhiteSpace(_incident.NativeAction)
                && !string.IsNullOrWhiteSpace(_incident.NativeContextHash) && !string.IsNullOrWhiteSpace(_incident.NativeContextDescription)
                && !string.IsNullOrWhiteSpace(_incident.RequestHash) && !string.IsNullOrWhiteSpace(revision);
            var rule = SelectedRule?.Rule;
            _canRevoke = rule?.Enabled == true && context?.IsAvailable == true && context.ModelScopeKey == rule.ModelScopeKey;
            foreach (var command in _decisions.Values) command.RaiseCanExecuteChanged();
            RevokeCommand.RaiseCanExecuteChanged(); CopyCommand.RaiseCanExecuteChanged();
            ActionStateVersion++;
            OnPropertyChanged(nameof(ActionStateVersion)); PublishStatus();
        }
        private bool Allowed(string action) => action == "accept_once" ? _canAccept : action == "create_rule" ? _canRule : action == "native_grant" ? _canNative : _canRework;
        private void SetBanner(string text, UiTone tone = UiTone.Neutral)
        {
            PageStatus = text ?? ""; _bannerTone = tone;
            OnPropertyChanged(nameof(PageStatus)); PublishStatus();
        }
        private void PublishStatus() => SetStatus(string.Join(" · ", new[] { PageStatus, DecisionHint }.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text.Trim())),
            _bannerTone == UiTone.Error ? UiTone.Error : string.IsNullOrWhiteSpace(DecisionHint) ? _bannerTone : UiTone.Review);
        public override void Dispose()
        {
            if (_disposed) return;
            Deactivate(); _disposed = true;
            _runtime.ContextChanged -= OnContextChanged; _runtime.LanguageChanged -= OnLanguageChanged;
            _runtime.Dispose();
            foreach (var command in _decisions.Values) command.RaiseCanExecuteChanged();
            foreach (var command in new[] { RefreshCommand, ResetCommand, RevokeCommand, CopyCommand }) command.RaiseCanExecuteChanged();
        }
    }
}
