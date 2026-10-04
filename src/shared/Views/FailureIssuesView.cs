using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RvtMcp.Failures;
using RvtMcp.Plugin.Failures;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views
{
    /// <summary>Modeless local ledger review. All Revit facts are captured by FailureControl on its API thread.</summary>
    internal sealed class FailureIssuesView : UserControl
    {
        private const int MessagePageSize = 50;
        private readonly ComboBox _models = new ComboBox { MinWidth = 210, MaxWidth = 340, DisplayMemberPath = "Title" };
        private readonly ComboBox _states = new ComboBox { MinWidth = 130 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        private readonly TextBlock _pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _messagePageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly DataGrid _grid;
        private readonly TextBox _details = new TextBox {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.White, BorderThickness = new Thickness(0), MinHeight = 100
        };
        private readonly TextBox _reason = new TextBox { MinHeight = 30, MaxLength = 2000, TextWrapping = TextWrapping.Wrap };
        private readonly CheckBox _verified = new CheckBox { Margin = new Thickness(0, 5, 0, 5) };
        private readonly CheckBox _dialogConsent = new CheckBox { Margin = new Thickness(0, 5, 0, 5), Visibility = Visibility.Collapsed };
        private readonly ListBox _rules = new ListBox { MaxHeight = 140, DisplayMemberPath = "Label" };
        private readonly List<Action> _localizers = new List<Action>();
        private readonly List<Button> _decisionButtons = new List<Button>();
        private readonly Stack<string> _previousCursors = new Stack<string>();
        private readonly Button _previous;
        private readonly Button _next;
        private readonly Button _messagePrevious;
        private readonly Button _messageNext;
        private readonly Button _accept;
        private readonly Button _createRule;
        private readonly Button _native;
        private readonly Button _revoke;
        private readonly EventHandler _languageChanged;
        private string _cursor;
        private string _nextCursor;
        private string _lastScope;
        private bool _loading;
        private int _messagePage;
        private FailureIncident _incident;
        private FailureOperation _operation;

        internal FailureIssuesView()
        {
            var root = new Grid { Margin = new Thickness(8) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.7, GridUnitType.Star), MinHeight = 90, MaxHeight = 180 });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            toolbar.Children.Add(Label("failure.filter.model"));
            toolbar.Children.Add(_models);
            toolbar.Children.Add(Label("failure.filter.state"));
            toolbar.Children.Add(_states);
            foreach (var state in new[] { "", "open", "observed", "resolved_rework", "resolved_abandoned", "accepted_once", "accepted_by_rule" })
                _states.Items.Add(new ComboBoxItem { Tag = state, Content = state.Length == 0 ? L.T("failure.filter.all") : L.T("failure.state." + state) });
            _states.SelectedIndex = 1;
            toolbar.Children.Add(Button("failure.refresh", (s, e) => Refresh()));
            _previous = Button("failure.previous", (s, e) => {
                if (_previousCursors.Count == 0) return;
                _cursor = _previousCursors.Pop(); LoadPage();
            });
            _next = Button("failure.next", (s, e) => {
                if (_nextCursor == null) return;
                _previousCursors.Push(_cursor); _cursor = _nextCursor; LoadPage();
            });
            toolbar.Children.Add(_previous);
            toolbar.Children.Add(_next);
            toolbar.Children.Add(_pageLabel);
            Add(root, toolbar, 0);
            Add(root, _status, 1);

            _grid = new DataGrid {
                IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Margin = new Thickness(0, 4, 0, 4)
            };
            Column("failure.col.time", "Time", 140);
            Column("failure.col.tool", "Tool", 175);
            Column("failure.col.severity", "Severity", 80);
            Column("failure.col.count", "Count", 75);
            Column("failure.col.outcome", "Outcome", 115);
            Column("failure.col.state", "State", 140);
            _grid.SelectionChanged += (s, e) => SelectIssue((_grid.SelectedItem as IssueRow)?.Incident);
            Add(root, _grid, 2);

            var detail = new Grid();
            detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var messageToolbar = new WrapPanel();
            messageToolbar.Children.Add(Button("failure.copy", (s, e) => CopyDetails()));
            _messagePrevious = Button("failure.previous", (s, e) => { _messagePage--; RenderDetails(); });
            _messageNext = Button("failure.next", (s, e) => { _messagePage++; RenderDetails(); });
            messageToolbar.Children.Add(_messagePrevious);
            messageToolbar.Children.Add(_messageNext);
            messageToolbar.Children.Add(_messagePageLabel);
            Add(detail, messageToolbar, 0);
            Add(detail, _details, 1);
            Add(root, detail, 3);

            var decisions = new StackPanel();
            decisions.Children.Add(Label("security.failure.reason"));
            decisions.Children.Add(_reason);
            _localizers.Add(() => _verified.Content = L.T("security.failure.verified"));
            decisions.Children.Add(_verified);
            decisions.Children.Add(_dialogConsent);
            var actions = new WrapPanel();
            actions.Children.Add(DecisionButton("security.failure.rework", "rework"));
            actions.Children.Add(DecisionButton("security.failure.abandon", "abandon"));
            _accept = DecisionButton("security.failure.acceptOnce", "accept_once");
            _createRule = DecisionButton("security.failure.createRule", "create_rule");
            _native = DecisionButton("security.failure.nativeGrant", "native_grant");
            actions.Children.Add(_accept);
            actions.Children.Add(_createRule);
            actions.Children.Add(_native);
            decisions.Children.Add(actions);
            var rulesPanel = new StackPanel();
            rulesPanel.Children.Add(_rules);
            _revoke = Button("security.failure.revokeRule", (s, e) => RevokeRule());
            _rules.SelectionChanged += (s, e) => UpdateActions();
            rulesPanel.Children.Add(_revoke);
            var ruleExpander = new Expander { Content = rulesPanel, Margin = new Thickness(0, 6, 0, 0) };
            _localizers.Add(() => ruleExpander.Header = L.T("failure.rules"));
            decisions.Children.Add(ruleExpander);
            Add(root, new ScrollViewer { Content = decisions, MaxHeight = 230,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 4);
            Content = root;
            _models.SelectionChanged += (s, e) => { if (!_loading) { ResetPage(); LoadPage(); } };
            _states.SelectionChanged += (s, e) => { if (!_loading) { ResetPage(); LoadPage(); } };
            _languageChanged = (s, e) => {
                if (Dispatcher.HasShutdownStarted) return;
                if (Dispatcher.CheckAccess()) ApplyLocalization();
                else Dispatcher.BeginInvoke(new Action(ApplyLocalization));
            };
            Loaded += (s, e) => { L.Changed += _languageChanged; ApplyLocalization(); Refresh(); };
            Unloaded += (s, e) => L.Changed -= _languageChanged;
            ApplyLocalization();
        }

        internal void Refresh()
        {
            _loading = true;
            var wanted = (_models.SelectedItem as FailureModel)?.ModelScopeKey;
            try
            {
                var context = FailureControl.GetUiContext();
                var models = Reader().GetModels();
                if (context?.IsAvailable == true && models.All(m => m.ModelScopeKey != context.ModelScopeKey))
                    models.Insert(0, new FailureModel { ModelScopeKey = context.ModelScopeKey, Title = context.ModelTitle });
                _models.ItemsSource = models;
                _models.SelectedItem = models.FirstOrDefault(m => m.ModelScopeKey == wanted)
                    ?? models.FirstOrDefault(m => m.ModelScopeKey == context?.ModelScopeKey) ?? models.FirstOrDefault();
                ResetPage();
            }
            catch (Exception ex) { ShowFailure(ex.Message); }
            finally { _loading = false; }
            LoadPage();
        }

        private static FailureLedgerStore Reader() => new FailureLedgerStore(readOnly: true);

        private void ResetPage()
        {
            _cursor = null; _nextCursor = null; _previousCursors.Clear();
        }

        private void LoadPage()
        {
            if (_loading) return;
            var model = _models.SelectedItem as FailureModel;
            if (model == null)
            {
                _grid.ItemsSource = null;
                _rules.ItemsSource = null;
                _status.Text = L.T("failure.noModel");
                SelectIssue(null); UpdatePaging(0); return;
            }
            if (_lastScope != model.ModelScopeKey)
            {
                _reason.Clear(); _verified.IsChecked = false; _lastScope = model.ModelScopeKey;
            }
            try
            {
                var reader = Reader();
                var state = (_states.SelectedItem as ComboBoxItem)?.Tag as string;
                var page = reader.Query(new FailureRecordQuery {
                    ModelScopeKey = model.ModelScopeKey, State = string.IsNullOrEmpty(state) ? null : state,
                    Cursor = _cursor, Limit = 50, IncludeDetails = false
                });
                var operations = new Dictionary<string, FailureOperation>();
                var rows = new List<IssueRow>();
                foreach (var incident in page.Records)
                {
                    FailureOperation operation = null;
                    if (incident.OperationId != null && !operations.TryGetValue(incident.OperationId, out operation))
                    {
                        operation = reader.GetOperation(incident.OperationId);
                        operations[incident.OperationId] = operation;
                    }
                    rows.Add(new IssueRow(incident, operation));
                }
                var selectedId = _incident?.IncidentId;
                _grid.ItemsSource = rows;
                _nextCursor = page.NextCursor;
                _grid.SelectedItem = rows.FirstOrDefault(r => r.Incident.IncidentId == selectedId) ?? rows.FirstOrDefault();
                var gate = FailureControl.GetUiGate(model.ModelScopeKey);
                _status.Text = page.Available
                    ? L.T(gate.Blocked ? "failure.gate.blocked" : "failure.gate.clear")
                    : L.T("failure.unavailable");
                var active = FailureControl.GetUiContext();
                _status.Text = (active?.IsAvailable == true
                    ? L.T("failure.activeModel", ("title", active.ModelTitle ?? Unknown())) : L.T("failure.noModel"))
                    + "\n" + _status.Text;
                if (!page.Complete) _status.Text += "\n" + L.T("failure.detail.incomplete");
                if (rows.Count == 0 && page.Available) _status.Text += "\n" + L.T("failure.empty");
                _status.Foreground = gate.Blocked || !page.Available ? Brushes.Firebrick : Brushes.DimGray;
                _rules.ItemsSource = reader.GetRules(model.ModelScopeKey).Select(rule => new RuleRow(rule)).ToList();
                _rules.ToolTip = L.T("failure.rules.none");
                UpdatePaging(rows.Count); UpdateActions();
            }
            catch (Exception ex)
            {
                // Staged reason is deliberately retained on IO/corruption failures.
                _grid.ItemsSource = null; _rules.ItemsSource = null;
                SelectIssue(null); _nextCursor = null; UpdatePaging(0); ShowFailure(ex.Message);
            }
        }

        private void SelectIssue(FailureIncident selected)
        {
            var same = selected != null && selected.IncidentId == _incident?.IncidentId;
            if (selected != null && _incident != null && !same) _reason.Clear();
            _incident = null; _operation = null; _messagePage = 0;
            if (!same) { _verified.IsChecked = false; _dialogConsent.IsChecked = false; }
            if (selected != null)
            {
                try
                {
                    var reader = Reader();
                    _incident = reader.Query(new FailureRecordQuery {
                        ModelScopeKey = selected.ModelScopeKey, IncidentId = selected.IncidentId,
                        IncludeDetails = true, Limit = 1
                    }).Records.FirstOrDefault();
                    _operation = reader.GetOperation(selected.OperationId);
                }
                catch (Exception ex) { ShowFailure(ex.Message); }
            }
            RenderDetails(); UpdateActions();
        }

        private void RenderDetails()
        {
            if (_incident == null)
            {
                _details.Text = L.T("failure.detail.prompt");
                _messagePageLabel.Text = "";
                _messagePrevious.IsEnabled = _messageNext.IsEnabled = false;
                return;
            }
            var total = _incident.Messages?.Count ?? 0;
            _messagePage = Math.Max(0, Math.Min(_messagePage, Math.Max(0, (total - 1) / MessagePageSize)));
            _details.Text = DetailsText(false);
            _messagePageLabel.Text = L.T("failure.detail.messagePage",
                ("first", total == 0 ? 0 : _messagePage * MessagePageSize + 1),
                ("last", Math.Min(total, (_messagePage + 1) * MessagePageSize)), ("total", total));
            _messagePrevious.IsEnabled = _messagePage > 0;
            _messageNext.IsEnabled = (_messagePage + 1) * MessagePageSize < total;
        }

        private string DetailsText(bool allMessages)
        {
            if (_incident == null) return "";
            var text = new StringBuilder();
            text.AppendLine(_incident.ModelTitle ?? (_models.SelectedItem as FailureModel)?.Title);
            text.AppendLine(L.T("failure.detail.identity", ("incident", _incident.IncidentId),
                ("operation", _incident.OperationId), ("scope", _incident.ModelScopeKey)));
            text.AppendLine((_operation?.Tool ?? Unknown()) + " · " + (_operation?.Execution ?? Unknown())
                + " · " + (_operation?.Outcome ?? Unknown()) + " · " + _incident.State);
            text.AppendLine(L.T("failure.detail.context", ("context", _incident.OperationContext ?? Unknown()),
                ("year", _incident.RevitYear), ("transaction", _incident.TransactionName ?? Unknown()),
                ("coverage", _operation?.ObservationCoverage ?? Unknown()),
                ("complete", _incident.Complete && _operation?.Complete == true), ("persistence", _incident.Persistence)));
            if (FailureIssuesPresentation.NeedsModelVerification(_operation)) text.AppendLine(L.T("failure.detail.partial"));
            if (!_incident.Complete || _operation?.Complete != true) text.AppendLine(L.T("failure.detail.incomplete"));
            text.AppendLine(L.T("failure.detail.temporary"));
            text.AppendLine("Request: " + _incident.RequestHash + "\nRevision: " + _incident.DocumentRevision);
            if (!string.IsNullOrEmpty(_incident.NativeAction))
            {
                text.AppendLine(_incident.NativeAction);
                text.AppendLine(_incident.NativeContextDescription);
                text.AppendLine("Context fingerprint: " + _incident.NativeContextHash);
            }
            var messages = _incident.Messages ?? new List<FailureMessage>();
            IEnumerable<FailureMessage> shown = allMessages ? messages : messages.Skip(_messagePage * MessagePageSize).Take(MessagePageSize);
            foreach (var message in shown)
            {
                text.AppendLine();
                text.AppendLine(message.Severity + " · " + message.DefinitionId);
                text.AppendLine(message.Description);
                var ids = message.FailingElementIds.Concat(message.AdditionalElementIds).Distinct().ToList();
                text.AppendLine(L.T("failure.detail.elements", ("ids", string.Join(", ", allMessages ? ids : ids.Take(200)))));
                if (!allMessages && ids.Count > 200)
                    text.AppendLine(L.T("failure.detail.moreIds", ("count", ids.Count - 200)));
                if (!message.Complete || message.TextTruncated) text.AppendLine(L.T("failure.detail.incomplete"));
                foreach (var snapshot in message.Snapshots.Take(allMessages ? int.MaxValue : 20))
                    text.AppendLine(snapshot.ElementId + " · " + snapshot.Category + " · " + snapshot.Type
                        + " · " + snapshot.Level + " · existsAfterRollback=" + (snapshot.ExistsAfterRollback?.ToString() ?? Unknown()));
            }
            if (FailureIssuesPresentation.CanAcceptWarning(_incident))
            {
                text.AppendLine();
                foreach (var rule in FailureIssuesPresentation.WarningConstraints(_incident)) text.AppendLine(RuleText(rule));
            }
            return text.ToString();
        }

        private static string RuleText(FailureRule rule) => (rule.Severity == "TaskDialog" ? ProtectedRuleText(rule, null, null) : L.T("failure.detail.rule",
            ("definition", rule.DefinitionId), ("severity", rule.Severity), ("context", rule.OperationContext),
            ("year", rule.RevitYear), ("count", rule.MaxCount), ("elements", string.Join(", ", rule.ExistingElementIds))))
            + " · " + L.T(rule.Enabled ? "failure.rule.enabled" : "failure.rule.revoked") + " · v" + rule.Revision;

        private void RecordDecision(string action)
        {
            if (_incident == null) return;
            try
            {
                var context = action == "native_grant" ? FailureControl.GetUiContext(_incident.ModelScopeKey) : FailureControl.GetUiContext();
                var dialog = FailureIssuesPresentation.DialogAdapter(_incident);
                if (dialog != null && (action == "accept_once" || action == "create_rule") && _dialogConsent.IsChecked != true)
                    throw new InvalidOperationException("security.failure.dialogChoiceRequired");
                var decision = action == "native_grant"
                    ? NativeDecision(context)
                    : FailureIssuesPresentation.CreateDecision(action, _incident, _operation, context,
                        _reason.Text, _verified.IsChecked == true);
                var warning = action == "accept_once" ? L.T(dialog != null ? "security.failure.dialogOnceWarning" : "security.failure.onceWarning")
                    : action == "create_rule" ? L.T(dialog != null ? "security.failure.dialogRuleWarning" : "security.failure.ruleWarning")
                    : action == "native_grant" ? L.T("security.failure.nativeWarning") : "";
                var label = dialog != null && action == "accept_once" ? L.T("security.failure.acceptDialogOnce") : ActionLabel(action);
                if (!Confirm(label, warning + "\n\n" + ProtectedConstraints(decision, context)
                    + "\n\n" + DetailsText(true), decision.Reason)) return;
                SaveDecision(decision, context);
            }
            catch (Exception ex) { ShowDecisionFailure(ex); }
        }

        private FailureDecision NativeDecision(FailureIssuesContext context)
        {
            var decision = FailureIssuesPresentation.CreateDecision("rework", _incident, _operation, context,
                _reason.Text, _verified.IsChecked == true);
            if (!_incident.Complete || string.IsNullOrWhiteSpace(_incident.NativeAction) || string.IsNullOrWhiteSpace(_incident.NativeContextHash)
                || string.IsNullOrWhiteSpace(_incident.NativeContextDescription) || string.IsNullOrWhiteSpace(_incident.RequestHash)
                || string.IsNullOrWhiteSpace(context.DocumentRevision) || context.RevitYear != _incident.RevitYear)
                throw new InvalidOperationException("security.failure.stale");
            if (context.DocumentRevision != _incident.DocumentRevision && _verified.IsChecked != true)
                throw new InvalidOperationException("security.failure.revisionVerificationRequired");
            decision.Action = "native_grant";
            decision.Grant = new FailureGrant {
                ModelScopeKey = _incident.ModelScopeKey, RevitYear = _incident.RevitYear,
                OperationContext = _incident.OperationContext, NativeAction = _incident.NativeAction,
                NativeContextHash = _incident.NativeContextHash, RequestHash = _incident.RequestHash,
                DocumentRevision = context.DocumentRevision, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            };
            return decision;
        }

        private void RevokeRule()
        {
            var rule = (_rules.SelectedItem as RuleRow)?.Rule;
            if (rule == null || !rule.Enabled) return;
            try
            {
                var context = FailureControl.GetUiContext();
                if (context?.IsAvailable != true || context.ModelScopeKey != rule.ModelScopeKey)
                    throw new InvalidOperationException("security.failure.currentModelRequired");
                if (string.IsNullOrWhiteSpace(_reason.Text)) throw new InvalidOperationException("security.failure.reasonRequired");
                if (!Confirm(L.T("security.failure.revokeRule"), ProtectedRuleText(rule, null, context.DocumentRevision), _reason.Text)) return;
                SaveDecision(new FailureDecision {
                    ModelScopeKey = rule.ModelScopeKey, Action = "revoke_rule", Rule = rule, Reason = _reason.Text.Trim(),
                    WindowsUser = Environment.UserName, RevitUser = context.RevitUser, HostInstanceId = context.HostInstanceId,
                    Channel = "revit_ui", DocumentRevision = context.DocumentRevision
                }, context);
            }
            catch (Exception ex) { ShowDecisionFailure(ex); }
        }

        private void SaveDecision(FailureDecision decision, FailureIssuesContext reviewedContext)
        {
            var current = decision.Action == "native_grant" ? FailureControl.GetUiContext(decision.ModelScopeKey) : FailureControl.GetUiContext();
            if (current?.IsAvailable != true || current.ModelScopeKey != reviewedContext.ModelScopeKey
                || current.DocumentRevision != reviewedContext.DocumentRevision
                || current.RevitYear != reviewedContext.RevitYear || current.HostInstanceId != reviewedContext.HostInstanceId)
                throw new InvalidOperationException("security.failure.stale");
            var result = FailureControl.ApplyUiDecision(decision);
            if (result == null || !result.Recorded || !result.Durable)
            {
                ShowFailure(result?.ErrorCode ?? "FAILURE_STORAGE_FAILED");
                return;
            }
            _reason.Clear(); _verified.IsChecked = false; _dialogConsent.IsChecked = false;
            LoadPage();
            _status.Text = L.T("failure.decision.saved") + "\n" + _status.Text;
        }

        private string ProtectedConstraints(FailureDecision decision, FailureIssuesContext context)
        {
            // Decision facts must use locked security keys; ordinary user-overridable labels cannot hide a constraint.
            if (decision.Action == "native_grant")
                return L.T("security.failure.nativeConstraints", ("scope", decision.ModelScopeKey),
                    ("action", decision.Grant.NativeAction), ("details", _incident.NativeContextDescription),
                    ("year", decision.Grant.RevitYear), ("context", decision.Grant.OperationContext),
                    ("fingerprint", decision.Grant.NativeContextHash), ("request", decision.Grant.RequestHash),
                    ("revision", decision.Grant.DocumentRevision));
            var rules = decision.Rules.Cast<FailureRule>().Concat(decision.Grants).ToList();
            return string.Join("\n\n", rules.Select(rule => ProtectedRuleText(rule, _incident.RequestHash, context.DocumentRevision)));
        }

        private static string ProtectedRuleText(FailureRule rule, string request, string revision)
        {
            var adapter = rule.Severity == "TaskDialog" ? FailureDialogAdapters.Get(rule.DefinitionId, rule.RevitYear, rule.OperationContext) : null;
            if (adapter != null)
                return L.T("security.failure.dialogConstraints", ("scope", rule.ModelScopeKey), ("dialog", adapter.DialogId),
                    ("year", rule.RevitYear), ("context", adapter.OperationContext), ("semantic", DialogSemantic(adapter)),
                    ("result", adapter.Result), ("request", request ?? "—"), ("revision", revision ?? "—"));
            return L.T("security.failure.constraints", ("scope", rule.ModelScopeKey), ("definition", rule.DefinitionId),
                ("year", rule.RevitYear), ("context", rule.OperationContext), ("count", rule.MaxCount),
                ("elements", string.Join(", ", rule.ExistingElementIds)), ("request", request ?? "—"), ("revision", revision ?? "—"));
        }

        private static string DialogSemantic(FailureDialogAdapter adapter) => L.T(adapter.DialogId == "TaskDialog_Unresolved_References"
            ? "security.failure.dialogUnresolved" : "security.failure.dialogAnalytical");

        private bool Confirm(string action, string details, string reason)
        {
            // Scrollable owned review, rather than a message box that can run off-screen for many failures.
            var owner = Window.GetWindow(this);
            var window = new Window {
                Title = L.T("security.failure.confirmTitle"), Owner = owner, Width = 650, Height = 540,
                MinWidth = 420, MinHeight = 320, MaxHeight = SystemParameters.WorkArea.Height,
                MaxWidth = SystemParameters.WorkArea.Width, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false
            };
            window.Resources.MergedDictionaries.Add(BimwrightStyles.Dictionary);
            var panel = new DockPanel { Margin = new Thickness(12) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = L.T("update.close"), IsCancel = true, Margin = new Thickness(4) };
            var approve = new Button { Content = action, Margin = new Thickness(4), MinWidth = 130 };
            approve.Click += (s, e) => window.DialogResult = true;
            buttons.Children.Add(cancel); buttons.Children.Add(approve);
            DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            panel.Children.Add(new TextBox {
                Text = L.T("security.failure.confirm", ("action", action), ("details", details), ("reason", reason)),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0), Background = Brushes.White
            });
            window.Content = panel;
            return window.ShowDialog() == true;
        }

        private void UpdateActions()
        {
            var context = FailureControl.GetUiContext();
            var sameModel = context?.IsAvailable == true && context.ModelScopeKey == _incident?.ModelScopeKey;
            var settled = _operation == null || _operation.Execution == "completed" || _operation.Execution == "interrupted_unknown";
            var active = sameModel && settled && _incident?.State == "open";
            foreach (var button in _decisionButtons) button.IsEnabled = active;
            var warning = active && FailureIssuesPresentation.CanAcceptWarning(_incident);
            var dialog = FailureIssuesPresentation.DialogAdapter(_incident);
            _accept.IsEnabled = _createRule.IsEnabled = warning || (active && dialog != null);
            if (sameModel && settled && _incident?.State == "observed" && dialog != null) _createRule.IsEnabled = true;
            _accept.Content = L.T(dialog == null ? "security.failure.acceptOnce" : "security.failure.acceptDialogOnce");
            _accept.ToolTip = L.T(dialog != null ? "security.failure.dialogOnceWarning" : warning ? "security.failure.onceWarning" : "security.failure.warningOnly");
            _createRule.ToolTip = L.T(dialog != null ? "security.failure.dialogRuleWarning" : warning ? "security.failure.ruleWarning" : "security.failure.warningOnly");
            _dialogConsent.Visibility = dialog == null ? Visibility.Collapsed : Visibility.Visible;
            _dialogConsent.Content = dialog == null ? "" : L.T("security.failure.dialogChoice", ("semantic", DialogSemantic(dialog)));
            _native.Visibility = string.IsNullOrWhiteSpace(_incident?.NativeAction) ? Visibility.Collapsed : Visibility.Visible;
            var nativeContext = _incident == null ? null : FailureControl.GetUiContext(_incident.ModelScopeKey);
            _native.IsEnabled = nativeContext?.IsAvailable == true && nativeContext.ModelScopeKey == _incident?.ModelScopeKey
                && settled && _incident?.State == "open" && _incident.Complete && !string.IsNullOrWhiteSpace(_incident.NativeContextHash)
                && !string.IsNullOrWhiteSpace(_incident.NativeContextDescription);
            var rule = (_rules.SelectedItem as RuleRow)?.Rule;
            _revoke.IsEnabled = rule?.Enabled == true && context?.IsAvailable == true && context.ModelScopeKey == rule.ModelScopeKey;
        }

        private void UpdatePaging(int count)
        {
            _previous.IsEnabled = _previousCursors.Count > 0;
            _next.IsEnabled = _nextCursor != null;
            _pageLabel.Text = L.T("failure.page", ("page", _previousCursors.Count + 1), ("count", count));
        }

        private void ApplyLocalization()
        {
            foreach (var apply in _localizers) apply();
            foreach (ComboBoxItem item in _states.Items)
                item.Content = (string)item.Tag == "" ? L.T("failure.filter.all") : L.T("failure.state." + item.Tag);
            if (_grid != null) { _grid.Items.Refresh(); UpdatePaging(_grid.Items.Count); }
            _rules.Items.Refresh();
            if (_messagePrevious != null) RenderDetails();
            if (_accept != null) UpdateActions();
        }

        private void CopyDetails()
        {
            if (_incident == null) return;
            try
            {
                Clipboard.SetText(DetailsText(true) + "\n\n" + FailureJson.Serialize(_incident));
                _status.Text = L.T("failure.copied");
            }
            catch (Exception ex) { ShowFailure(ex.Message); }
        }

        private void ShowDecisionFailure(Exception error)
        {
            _status.Text = error.Message.StartsWith("security.failure.", StringComparison.Ordinal)
                ? L.T(error.Message) : L.T("security.failure.saveFailed", ("error", error.Message));
            _status.Foreground = Brushes.Firebrick;
        }

        private void ShowFailure(string error)
        {
            _status.Text = L.T("security.failure.saveFailed", ("error", error));
            _status.Foreground = Brushes.Firebrick;
        }

        private Button DecisionButton(string key, string action)
        {
            var button = Button(key, (s, e) => RecordDecision(action));
            _decisionButtons.Add(button); return button;
        }

        private Button Button(string key, RoutedEventHandler click)
        {
            var button = new Button { Margin = new Thickness(4, 2, 4, 2) };
            _localizers.Add(() => button.Content = L.T(key));
            button.Click += click; return button;
        }

        private TextBlock Label(string key)
        {
            var label = new TextBlock { Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
            _localizers.Add(() => label.Text = L.T(key)); return label;
        }

        private void Column(string key, string property, double width)
        {
            var column = new DataGridTextColumn { Binding = new Binding(property), Width = width };
            _localizers.Add(() => column.Header = L.T(key)); _grid.Columns.Add(column);
        }

        private static void Add(Grid grid, UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
        private static string Unknown() => L.T("failure.detail.unknown");
        private static string ActionLabel(string action) => L.T("security.failure." +
            (action == "accept_once" ? "acceptOnce" : action == "create_rule" ? "createRule"
                : action == "native_grant" ? "nativeGrant" : action));

        private sealed class IssueRow
        {
            internal FailureIncident Incident { get; }
            public string Time => Incident.CreatedUtc.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
            public string Tool { get; }
            public string Severity => Incident.Severity;
            public int Count => Incident.MessageCount;
            private readonly string _outcome;
            public string Outcome => L.T("failure.outcome." + _outcome);
            public string State => L.T("failure.state." + Incident.State);
            internal IssueRow(FailureIncident incident, FailureOperation operation)
            { Incident = incident; Tool = operation?.Tool ?? Unknown(); _outcome = operation?.Outcome ?? "unknown"; }
        }

        private sealed class RuleRow
        {
            internal FailureRule Rule { get; }
            public string Label => RuleText(Rule);
            internal RuleRow(FailureRule rule) { Rule = rule; }
        }
    }
}
