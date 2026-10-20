using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        // Bounded read on the Revit UI thread; the status line says when older issues are left out.
        private const int MaxIssueRows = 500;
        private readonly FailureModelPicker _models = new FailureModelPicker { Height = 28, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        private TextBox _modelSearch => _models.SearchBox;
        private TextBlock _modelSearchHint => _models.SearchHint;
        private TextBlock _modelSearchEmpty => _models.EmptyHint;
        private readonly ComboBox _states = new ComboBox { Height = 28, MinWidth = 130, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 6) };
        private readonly DataGrid _grid;
        private readonly TextBlock _issueHeadline = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid _facts = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        private readonly StackPanel _warningCards = new StackPanel();
        private readonly ScrollViewer _warningScroll;
        private readonly Border _warningFadeTop;
        private readonly Border _warningFadeBottom;
        private readonly StackPanel _rulesPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        private readonly TextBlock _rulesEmpty = new TextBlock { Foreground = Brushes.Gray };
        private readonly TextBlock _decisionHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = DecisionHintBrush };
        private readonly TextBox _reason = new TextBox { Height = 28, MaxLength = 2000, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _reasonWatermark = new TextBlock {
            Margin = new Thickness(6, 0, 6, 0),
            Style = (Style)BimwrightStyles.Dictionary[BimwrightStyles.PlaceholderTextKey]
        };
        private readonly CheckBox _verified = new CheckBox();
        private readonly CheckBox _dialogConsent = new CheckBox { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        private readonly ListBox _rules = new ListBox { MaxHeight = 72, DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 0, 4) };
        private readonly List<Action> _localizers = new List<Action>();
        private readonly List<Button> _decisionButtons = new List<Button>();
        private readonly GroupBox _issuesGroup;
        private readonly GroupBox _detailGroup;
        private readonly GroupBox _decisionGroup;
        private readonly GroupBox _warningsGroup;
        private readonly Button _copyDetails;
        private Button _accept;
        private Button _createRule;
        private Button _native;
        private Button _rework;
        private Button _abandon;
        private readonly Button _revoke;
        private readonly EventHandler _languageChanged;
        private string _lastScope;
        private bool _loading;
        private FailureIncident _incident;
        private FailureOperation _operation;
        private static readonly SolidColorBrush DecisionHintBrush = Freeze(0x9A, 0x34, 0x12);
        private static readonly SolidColorBrush MutedBrush = Freeze(0x64, 0x74, 0x8B);
        private static readonly SolidColorBrush ErrorBrush = Freeze(0x99, 0x1B, 0x1B);
        private static readonly SolidColorBrush ErrorBackground = Freeze(0xFE, 0xF2, 0xF2);
        private static readonly SolidColorBrush ErrorBorder = Freeze(0xDC, 0x26, 0x26);
        private static readonly SolidColorBrush WarningBrush = Freeze(0x92, 0x40, 0x0E);
        private static readonly SolidColorBrush WarningBackground = Freeze(0xFF, 0xFB, 0xEB);
        private static readonly SolidColorBrush WarningBorderBrush = Freeze(0xD9, 0x77, 0x06);
        private static readonly SolidColorBrush DialogBrush = Freeze(0x1E, 0x40, 0xAF);
        private static readonly SolidColorBrush DialogBackground = Freeze(0xEF, 0xF6, 0xFF);
        private static readonly SolidColorBrush DialogBorder = Freeze(0x25, 0x63, 0xEB);
        private static readonly SolidColorBrush NoticeBackground = Freeze(0xF8, 0xFA, 0xFC);
        private static readonly SolidColorBrush NoticeBorder = Freeze(0x64, 0x74, 0x8B);

        internal FailureIssuesView()
        {
            _models.ItemTemplate = ModelTemplate();
            _models.IsTextSearchEnabled = false;
            _models.ItemContainerStyle = ModelSearchStyle();
            _models.DropDownOpened += (s, e) => ApplyOpenEmphasis();
            _modelSearch.TextChanged += (s, e) => {
                _modelSearchHint.Visibility = string.IsNullOrEmpty(_modelSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
                ApplyOpenEmphasis();
            };
            var root = new Grid { Margin = new Thickness(8, 6, 8, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });

            var toolbar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Button("failure.reset", (s, e) => ResetLedger()));
            actions.Children.Add(Button("failure.refresh", (s, e) => Refresh()));
            DockPanel.SetDock(actions, Dock.Right);
            toolbar.Children.Add(actions);
            var filters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            filters.Children.Add(Label("failure.filter.model"));
            _models.Width = 320;
            _models.Margin = new Thickness(0, 0, 8, 0);
            filters.Children.Add(_models);
            filters.Children.Add(Label("failure.filter.state"));
            foreach (var state in new[] { "", "open", "observed", "resolved_rework", "resolved_abandoned", "accepted_once", "accepted_by_rule", "accepted_by_policy" })
                _states.Items.Add(new ComboBoxItem { Tag = state, Content = state.Length == 0 ? L.T("failure.filter.all") : L.T("failure.state." + state) });
            _states.SelectedIndex = 1;
            _states.Margin = new Thickness(0, 0, 8, 0);
            filters.Children.Add(_states);
            DockPanel.SetDock(filters, Dock.Left);
            toolbar.Children.Add(filters);
            Add(root, toolbar, 0);

            _grid = new DataGrid {
                IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Margin = new Thickness(0)
            };
            BimwrightStyles.KeepSelectionHighlight(_grid);
            var header = new Style(typeof(DataGridColumnHeader));
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            _grid.ColumnHeaderStyle = header;
            var center = new Style(typeof(TextBlock));
            center.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            var trim = new Style(typeof(TextBlock));
            trim.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            Column("failure.col.number", "Number", 0.4, 36, center);
            Column("failure.col.tool", "Tool", 2.2, 100, trim);
            Column("failure.col.date", "Date", 1.15, 88, center);
            Column("failure.col.time", "Clock", 0.9, 72, center);
            Column("failure.col.warnings", "Count", 0.7, 72, center);
            _grid.SelectionChanged += (s, e) => SelectIssue((_grid.SelectedItem as IssueRow)?.Incident);
            _issuesGroup = new GroupBox { Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8), Content = _grid };

            _facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 48 });
            _facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 48 });
            var factsOpen = false;
            var expand = IconButton("failure.group.record", "\uE70D", null);
            expand.Margin = new Thickness(8, 0, 0, 0);
            void UpdateExpand()
            {
                expand.Content = factsOpen ? "\uE70E" : "\uE70D";
                var action = L.T(factsOpen ? "failure.group.collapse" : "failure.group.record");
                System.Windows.Automation.AutomationProperties.SetName(expand, action);
                HistoryChrome.Guide(expand, action + "\n" + L.T("failure.record.tip"));
            }
            expand.Click += (s, e) =>
            {
                factsOpen = !factsOpen;
                _facts.Visibility = factsOpen ? Visibility.Visible : Visibility.Collapsed;
                UpdateExpand();
            };
            _facts.Visibility = Visibility.Collapsed;
            _facts.Margin = new Thickness(0, 6, 0, 0);
            var titleRow = new DockPanel { LastChildFill = true };
            _copyDetails = IconButton("failure.copy", "\uE8C8", (s, e) => CopyDetails());
            _copyDetails.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(expand, Dock.Right);
            titleRow.Children.Add(expand);
            DockPanel.SetDock(_copyDetails, Dock.Right);
            titleRow.Children.Add(_copyDetails);
            titleRow.Children.Add(_issueHeadline);
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            heading.Children.Add(titleRow);
            heading.Children.Add(_facts);

            _warningScroll = new ScrollViewer {
                Content = _warningCards, Background = Brushes.White, PanningMode = PanningMode.VerticalOnly,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _warningScroll.PreviewMouseWheel += (s, e) => HistoryChrome.Wheel(_warningScroll, e, 0.45);
            _warningScroll.ScrollChanged += (s, e) => UpdateWarningFades();
            _warningScroll.SizeChanged += (s, e) => UpdateWarningFades();
            _warningFadeTop = HistoryChrome.FadeEdge(true);
            _warningFadeBottom = HistoryChrome.FadeEdge(false);
            var warningHost = new Grid { Background = Brushes.White };
            warningHost.Children.Add(_warningScroll);
            warningHost.Children.Add(_warningFadeTop);
            warningHost.Children.Add(_warningFadeBottom);
            _warningsGroup = new GroupBox { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 4, 8, 8), Background = Brushes.White, Content = warningHost };

            var rulesTitle = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            _rulesPanel.Children.Add(rulesTitle);
            _rulesPanel.Children.Add(_rulesEmpty);
            _rulesPanel.Children.Add(_rules);
            _revoke = Button("security.failure.revokeRule", (s, e) => RevokeRule());
            _revoke.HorizontalAlignment = HorizontalAlignment.Left;
            _rules.SelectionChanged += (s, e) => UpdateActions();
            _rulesPanel.Children.Add(_revoke);

            var detail = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
            detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Add(detail, heading, 0);
            Add(detail, _warningsGroup, 1);
            Add(detail, _rulesPanel, 2);
            _detailGroup = new GroupBox { Margin = new Thickness(4, 0, 0, 4), Padding = new Thickness(8), Content = detail };
            _decisionGroup = new GroupBox { Margin = new Thickness(4, 4, 0, 4), Padding = new Thickness(8), Content = BuildDecision() };
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.Children.Add(_detailGroup);
            Grid.SetRow(_decisionGroup, 1);
            right.Children.Add(_decisionGroup);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 360 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star), MinWidth = 420 });
            Grid.SetColumn(_issuesGroup, 0);
            body.Children.Add(_issuesGroup);
            Grid.SetColumn(right, 1);
            body.Children.Add(right);
            Add(root, body, 1);
            Content = root;

            _localizers.Add(() => _issuesGroup.Header = L.T("failure.tab.issues"));
            _localizers.Add(() => _detailGroup.Header = L.T("failure.group.detail"));
            _localizers.Add(() => _decisionGroup.Header = L.T("failure.group.decision"));
            _localizers.Add(() => _warningsGroup.Header = L.T("failure.group.warnings"));
            _localizers.Add(() => rulesTitle.Text = L.T("failure.rules"));
            _localizers.Add(() => _rulesEmpty.Text = L.T("failure.rules.empty"));
            _localizers.Add(UpdateExpand);
            _localizers.Add(() => HistoryChrome.Guide(_models, L.T("failure.tip.model")));
            _localizers.Add(() => {
                _modelSearchHint.Text = L.T("failure.modelSearch");
                HistoryChrome.Guide(_modelSearch, _modelSearchHint.Text);
                System.Windows.Automation.AutomationProperties.SetName(_modelSearch, _modelSearchHint.Text);
                _modelSearchEmpty.Text = L.T("failure.modelSearch.empty");
            });
            _localizers.Add(() => HistoryChrome.Guide(_states, L.T("failure.tip.state")));
            _localizers.Add(() => HistoryChrome.Guide(_grid, L.T("failure.detail.prompt")));
            _localizers.Add(() => HistoryChrome.Guide(_copyDetails, L.T("failure.copy")));
            _localizers.Add(() => _verified.Content = L.T("failure.review.short"));
            _localizers.Add(() => HistoryChrome.Guide(_verified, L.T("security.failure.verified") + "\n" + L.T("security.failure.verificationRequired")));
            _localizers.Add(() => HistoryChrome.Guide(_dialogConsent, L.T("security.failure.dialogChoiceRequired")));
            _localizers.Add(() => {
                var hint = L.T("security.failure.reasonPlaceholder");
                _reasonWatermark.Text = hint;
                HistoryChrome.Guide(_reason, hint);
                System.Windows.Automation.AutomationProperties.SetName(_reason, L.T("security.failure.reason"));
            });

            _models.SelectionChanged += (s, e) => { if (!_loading) LoadPage(); };
            _states.SelectionChanged += (s, e) => { if (!_loading) Refresh(); };
            _languageChanged = (s, e) => {
                if (Dispatcher.HasShutdownStarted) return;
                if (Dispatcher.CheckAccess()) ApplyLocalization();
                else Dispatcher.BeginInvoke(new Action(ApplyLocalization));
            };
            Loaded += (s, e) => { L.Changed += _languageChanged; FailureControl.UiContextChanged += OnUiContextChanged; ApplyLocalization(); Refresh(); };
            Unloaded += (s, e) => { L.Changed -= _languageChanged; FailureControl.UiContextChanged -= OnUiContextChanged; };
            ApplyLocalization();
        }

        private StackPanel BuildDecision()
        {
            var panel = new StackPanel();
            panel.Children.Add(_verified);
            panel.Children.Add(_dialogConsent);
            var reasonRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            reasonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var reasonLabel = Label("security.failure.reason");
            reasonLabel.FontWeight = FontWeights.SemiBold;
            reasonLabel.Margin = new Thickness(0, 0, 8, 0);
            reasonRow.Children.Add(reasonLabel);
            var reasonInput = HistoryChrome.WithWatermark(_reason, _reasonWatermark);
            Grid.SetColumn(reasonInput, 1);
            reasonRow.Children.Add(reasonInput);
            panel.Children.Add(reasonRow);
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            _rework = DecisionButton("failure.action.rework", "rework");
            _abandon = DecisionButton("failure.action.abandon", "abandon");
            _accept = DecisionButton("failure.action.acceptOnce", "accept_once");
            _createRule = DecisionButton("failure.action.addRule", "create_rule");
            _native = DecisionButton("failure.action.allowNative", "native_grant");
            actions.Children.Add(_rework);
            actions.Children.Add(_abandon);
            actions.Children.Add(_accept);
            actions.Children.Add(_createRule);
            actions.Children.Add(_native);
            panel.Children.Add(actions);
            _verified.Checked += (s, e) => UpdateActions();
            _verified.Unchecked += (s, e) => UpdateActions();
            return panel;
        }

        internal void Refresh()
        {
            _loading = true;
            try
            {
                var context = FailureControl.GetUiContext();
                var open = FailureControl.CopyOpenModelScopes();
                var state = (_states.SelectedItem as ComboBoxItem)?.Tag as string;
                var kept = _models.SelectedItem as ModelRow;
                var issues = Reader().GetIssueModels(string.IsNullOrEmpty(state) ? null : state);
                var models = issues.Select(m => new ModelRow(m, context, open)).ToList();
                if (kept != null && !models.Any(m => SameModel(m, kept)))
                {
                    var stillThere = Reader().GetIssueModels(null).FirstOrDefault(m =>
                        m.ModelScopeKey == kept.ModelScopeKey && m.RevitYear == kept.RevitYear);
                    if (stillThere != null)
                    {
                        stillThere.IssueCount = 0;
                        models.Add(new ModelRow(stillThere, context, open));
                    }
                }
                foreach (var model in models) model.UpdateSearch(_modelSearch.Text);
                models = OrderModels(models);
                _models.ItemsSource = models;
                var selected = kept == null ? null : models.FirstOrDefault(m => SameModel(m, kept));
                if (selected == null)
                {
                    var preferred = FailureIssuesPresentation.PreferredIssueModel(models.Select(m => m.Source).ToList(),
                        context.IsAvailable ? context.ModelScopeKey : null, context.RevitYear);
                    selected = models.FirstOrDefault(m => m.Source == preferred) ?? models.FirstOrDefault();
                }
                _models.SelectedItem = selected;
                UpdateSearchEmpty(models);
            }
            catch (Exception ex)
            {
                _models.ItemsSource = null; _grid.ItemsSource = null; _rules.ItemsSource = null;
                ShowRules(false); SelectIssue(null); ShowFailure(ex.Message); return;
            }
            finally { _loading = false; }
            LoadPage();
        }

        private static FailureLedgerStore Reader() => new FailureLedgerStore(readOnly: true);

        // Backs up the ledger and starts it empty after the user confirms what it unblocks.
        private void ResetLedger()
        {
            try
            {
                if (FailureControl.Store == null || FailureControl.Active != null) { SetStatus(L.T("failure.reset.busy"), Brushes.Firebrick); return; }
                var summary = FailureControl.GetResetSummary();
                if (!ConfirmReset(summary)) return;
                var backup = FailureControl.ResetLedger();
                Refresh();
                SetStatus(L.T("failure.reset.done", ("path", backup)), Brushes.DimGray);
            }
            catch (InvalidOperationException ex) when (ex.Message == "FAILURE_RESET_BUSY") { SetStatus(L.T("failure.reset.busy"), Brushes.Firebrick); }
            catch (Exception ex) { SetStatus(L.T("failure.reset.failed", ("error", ex.Message)), Brushes.Firebrick); }
        }

        private bool ConfirmReset(FailureLedgerSummary summary)
        {
            return new FailureResetDialog(L.T("failure.reset.confirm", ("issues", summary.IssueCount),
                ("open", summary.OpenIssueCount), ("models", summary.ModelCount), ("blocked", summary.BlockedModelCount),
                ("folder", FailureControl.Store.BackupFolder))) { Owner = Window.GetWindow(this) }.ShowDialog() == true;
        }

        private void ApplyOpenEmphasis()
        {
            if (!(_models.ItemsSource is IEnumerable<ModelRow> rows)) return;
            var context = FailureControl.GetUiContext();
            var open = FailureControl.CopyOpenModelScopes();
            foreach (var row in rows) { row.UpdateContext(context, open); row.UpdateSearch(_modelSearch.Text); }
            UpdateSearchEmpty(rows);
            var ordered = OrderModels(rows);
            if (ordered.SequenceEqual(rows)) return;
            var selected = _models.SelectedItem;
            var wasLoading = _loading;
            _loading = true;
            try { _models.ItemsSource = ordered; _models.SelectedItem = selected; }
            finally { _loading = wasLoading; }
        }

        private static bool SameModel(ModelRow row, ModelRow kept)
        {
            return row.ModelScopeKey == kept.ModelScopeKey && row.RevitYear == kept.RevitYear;
        }

        private static List<ModelRow> OrderModels(IEnumerable<ModelRow> rows)
        {
            return rows.OrderByDescending(row => row.SearchScore).ThenByDescending(row => row.IsOpen).ThenByDescending(row => row.IsCurrent)
                .ThenByDescending(row => row.RevitYear).ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.ModelScopeKey, StringComparer.Ordinal).ToList();
        }

        private void UpdateSearchEmpty(IEnumerable<ModelRow> rows)
        {
            _modelSearchEmpty.Visibility = !string.IsNullOrWhiteSpace(_modelSearch.Text) && !rows.Any(row => row.MatchesSearch)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnUiContextChanged(object sender, EventArgs args)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded) return;
                ApplyOpenEmphasis();
                UpdateActions();
            }));
        }

        private void LoadPage()
        {
            if (_loading) return;
            var model = _models.SelectedItem as ModelRow;
            if (model == null)
            {
                _grid.ItemsSource = null;
                _rules.ItemsSource = null;
                ShowRules(false);
                SetStatus(L.T("failure.empty"), Brushes.DimGray);
                SelectIssue(null); return;
            }
            var selectionKey = model.ModelScopeKey + ":" + model.RevitYear;
            if (_lastScope != selectionKey)
            {
                _reason.Clear(); _verified.IsChecked = false; _lastScope = selectionKey;
            }
            try
            {
                var reader = Reader();
                var state = (_states.SelectedItem as ComboBoxItem)?.Tag as string;
                var incidents = new List<FailureIncident>();
                string cursor = null;
                var available = false;
                var complete = true;
                var more = false;
                while (incidents.Count < MaxIssueRows)
                {
                    var page = reader.Query(new FailureRecordQuery {
                        ModelScopeKey = model.ModelScopeKey, RevitYear = model.RevitYear, State = string.IsNullOrEmpty(state) ? null : state,
                        Cursor = cursor, Limit = 100, IncludeDetails = false, NewestFirst = true
                    });
                    available = page.Available;
                    if (!page.Available) break;
                    complete &= page.Complete;
                    incidents.AddRange(page.Records);
                    cursor = page.NextCursor;
                    more = cursor != null;
                    if (!more) break;
                }
                var operations = new Dictionary<string, FailureOperation>();
                var rows = new List<IssueRow>();
                foreach (var incident in incidents)
                {
                    FailureOperation operation = null;
                    if (incident.OperationId != null && !operations.TryGetValue(incident.OperationId, out operation))
                    {
                        operation = reader.GetOperation(incident.OperationId);
                        operations[incident.OperationId] = operation;
                    }
                    rows.Add(new IssueRow(incident, operation) { Number = rows.Count + 1 });
                }
                // Rules first: selecting a row renders the Record facts, which read the rule count.
                var rules = reader.GetRules(model.ModelScopeKey).Select(rule => new RuleRow(rule)).ToList();
                _rules.ItemsSource = rules;
                ShowRules(rules.Count > 0);
                var selectedId = _incident?.IncidentId;
                _grid.ItemsSource = rows;
                _grid.SelectedItem = rows.FirstOrDefault(r => r.Incident.IncidentId == selectedId) ?? rows.FirstOrDefault();
                var gate = FailureControl.GetUiGate(model.ModelScopeKey);
                var parts = new List<string>();
                parts.Add(available ? L.T(gate.Blocked ? "failure.gate.blocked" : "failure.gate.clear") : L.T("failure.unavailable"));
                if (!complete) parts.Add(L.T("failure.detail.incomplete"));
                if (more) parts.Add(L.T("failure.list.newest", ("count", rows.Count)));
                if (rows.Count == 0 && available) parts.Add(L.T("failure.empty"));
                SetStatus(string.Join(" · ", parts), gate.Blocked || !available ? Brushes.Firebrick : Brushes.DimGray);
                UpdateActions();
            }
            catch (Exception ex)
            {
                // Staged reason is deliberately retained on IO/corruption failures.
                _grid.ItemsSource = null; _rules.ItemsSource = null;
                ShowRules(false);
                SelectIssue(null); ShowFailure(ex.Message);
            }
        }

        private void ShowRules(bool any)
        {
            _rules.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _rulesEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            _revoke.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _rulesPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SelectIssue(FailureIncident selected)
        {
            var same = selected != null && selected.IncidentId == _incident?.IncidentId;
            if (selected != null && _incident != null && !same) _reason.Clear();
            _incident = null; _operation = null;
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
            _warningCards.Children.Clear();
            _copyDetails.IsEnabled = _incident != null;
            if (_incident == null)
            {
                _issueHeadline.Text = "";
                ShowFacts(L.T("failure.detail.prompt"));
                _warningCards.Children.Add(Notice(L.T("failure.detail.prompt"), false));
                UpdateWarningFades();
                return;
            }
            var messages = _incident.Messages ?? new List<FailureMessage>();
            var severity = string.IsNullOrWhiteSpace(_incident.Severity) && messages.Count > 0 ? messages[0].Severity : _incident.Severity;
            var rules = _rules.ItemsSource as System.Collections.ICollection;
            var ruleText = rules == null || rules.Count == 0 ? L.T("failure.rules.noneLine") : rules.Count.ToString("n0", CultureInfo.CurrentCulture);
            _issueHeadline.Text = _operation?.Tool ?? Unknown();
            ShowFacts(
                L.T("failure.col.state"), L.T("failure.state." + _incident.State),
                L.T("failure.col.outcome"), L.T("failure.outcome." + (_operation?.Outcome ?? "unknown")),
                L.T("failure.col.severity"), severity,
                L.T("failure.col.count"), (_incident.MessageCount > 0 ? _incident.MessageCount : messages.Count).ToString("n0", CultureInfo.CurrentCulture),
                L.T("failure.fact.revit"), _incident.RevitYear > 0 ? _incident.RevitYear.ToString(CultureInfo.InvariantCulture) : "",
                L.T("failure.fact.context"), _incident.OperationContext,
                L.T("failure.fact.transaction"), _incident.TransactionName,
                L.T("failure.fact.execution"), _operation?.Execution,
                L.T("failure.fact.coverage"), _operation?.ObservationCoverage,
                L.T("failure.fact.evidence"), L.T(_incident.Complete && _operation?.Complete == true ? "failure.evidence.complete" : "failure.evidence.incomplete"),
                L.T("failure.fact.persistence"), _incident.Persistence,
                L.T("failure.fact.rules"), ruleText,
                L.T("security.warningPolicy.label"), _incident.WarningPolicy);
            if (FailureIssuesPresentation.NeedsModelVerification(_operation))
                _warningCards.Children.Add(Notice(L.T("failure.detail.partial"), true));
            if (!_incident.Complete || _operation?.Complete == false)
                _warningCards.Children.Add(Notice(L.T("failure.detail.incomplete"), true));
            if (!string.IsNullOrWhiteSpace(_incident.NativeAction))
                _warningCards.Children.Add(Notice(_incident.NativeAction + " · " + _incident.NativeContextDescription, false));
            if (_incident.Action == "capture_failed" && !string.IsNullOrEmpty(_incident.NativeContextDescription))
                _warningCards.Children.Add(Notice(_incident.NativeContextDescription, true));
            else if (messages.Count == 0)
                _warningCards.Children.Add(Notice(L.T("failure.detail.noWarnings"), false));
            for (var i = 0; i < messages.Count; i++)
                _warningCards.Children.Add(WarningCard(messages[i], i + 1, messages.Count));
            UpdateWarningFades();
        }

        private string DetailsText()
        {
            if (_incident == null) return "";
            var text = new StringBuilder();
            text.AppendLine(_incident.ModelTitle ?? (_models.SelectedItem as ModelRow)?.Title);
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
            text.AppendLine(L.T("security.warningPolicy.label") + ": " + _incident.WarningPolicy);
            if (!string.IsNullOrEmpty(_incident.NativeAction))
            {
                text.AppendLine(_incident.NativeAction);
                text.AppendLine(_incident.NativeContextDescription);
                text.AppendLine("Context fingerprint: " + _incident.NativeContextHash);
            }
            var messages = _incident.Messages ?? new List<FailureMessage>();
            foreach (var message in messages)
            {
                text.AppendLine();
                text.AppendLine(message.Severity + " · " + message.DefinitionId);
                text.AppendLine(message.Description);
                var ids = message.FailingElementIds.Concat(message.AdditionalElementIds).Distinct().ToList();
                text.AppendLine(L.T("failure.detail.elements", ("ids", string.Join(", ", ids))));
                if (!message.Complete || message.TextTruncated) text.AppendLine(L.T("failure.detail.incomplete"));
                foreach (var snapshot in message.Snapshots ?? new List<FailureElementSnapshot>())
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
                var context = FailureControl.GetUiContext(_incident.ModelScopeKey);
                var dialog = FailureIssuesPresentation.DialogAdapter(_incident);
                if (dialog != null && (action == "accept_once" || action == "create_rule") && _dialogConsent.IsChecked != true)
                    throw new InvalidOperationException("security.failure.dialogChoiceRequired");
                var decision = action == "native_grant"
                    ? FailureIssuesPresentation.CreateNativeDecision(_incident, _operation, context, _reason.Text, _verified.IsChecked == true)
                    : FailureIssuesPresentation.CreateDecision(action, _incident, _operation, context,
                        _reason.Text, _verified.IsChecked == true);
                var warning = action == "accept_once" ? L.T(dialog != null ? "security.failure.dialogOnceWarning" : "security.failure.onceWarning")
                    : action == "create_rule" ? L.T(dialog != null ? "security.failure.dialogRuleWarning" : "security.failure.ruleWarning")
                    : action == "native_grant" ? L.T("security.failure.nativeWarning") : "";
                var label = dialog != null && action == "accept_once" ? L.T("security.failure.acceptDialogOnce") : ActionLabel(action);
                if (FailureIssuesPresentation.RequiresConfirmation(action)
                    && !Confirm(label, warning, L.T("security.failure.review.permits"), ProtectedConstraints(decision, context), DetailsText(), decision.Reason)) return;
                SaveDecision(decision);
            }
            catch (Exception ex) { ShowDecisionFailure(ex); }
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
                var reason = FailureDecision.NormalizeReason(_reason.Text);
                if (!Confirm(L.T("security.failure.revokeRule"), "", L.T("failure.rules"), ProtectedRuleText(rule, null, context.DocumentRevision), "", reason)) return;
                SaveDecision(new FailureDecision {
                    ModelScopeKey = rule.ModelScopeKey, Action = "revoke_rule", Rule = rule, Reason = reason,
                    WindowsUser = Environment.UserName, RevitUser = context.RevitUser, HostInstanceId = context.HostInstanceId,
                    Channel = "revit_ui", DocumentRevision = context.DocumentRevision
                });
            }
            catch (Exception ex) { ShowDecisionFailure(ex); }
        }

        private void SaveDecision(FailureDecision decision)
        {
            var result = FailureControl.ApplyUiDecision(decision);
            if (result == null || !result.Recorded || !result.Durable)
            {
                ShowFailure(result?.ErrorCode ?? "FAILURE_STORAGE_FAILED");
                return;
            }
            _reason.Clear(); _verified.IsChecked = false; _dialogConsent.IsChecked = false;
            Refresh();
            SetStatus(L.T("failure.decision.saved") + " · " + _status.Text, _status.Foreground as Brush ?? Brushes.DimGray);
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
            return string.Join("\n\n", rules.Select(rule => ProtectedRuleText(rule, _incident.RequestHash, decision.DocumentRevision)));
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

        private bool Confirm(string action, string warning, string constraintsHeading, string constraints, string details, string reason)
        {
            // Scrollable owned review, rather than a message box that can run off-screen for many failures.
            return new FailureDecisionReviewWindow(Window.GetWindow(this), action, warning, constraintsHeading, constraints, details, reason).ShowDialog() == true;
        }

        private void UpdateActions()
        {
            var context = FailureControl.GetUiContext();
            var settled = _operation == null || _operation.Execution == "completed" || _operation.Execution == "interrupted_unknown";
            // Decisions belong to the selected incident, even when its model is inactive or closed.
            var active = settled && _incident?.State == "open";
            var reviewed = _verified.IsChecked == true;
            foreach (var button in _decisionButtons) button.IsEnabled = active && reviewed;
            var warning = active && FailureIssuesPresentation.CanAcceptWarning(_incident);
            var dialog = FailureIssuesPresentation.DialogAdapter(_incident);
            _accept.IsEnabled = _createRule.IsEnabled = reviewed && (warning || (active && dialog != null));
            if (settled && _incident?.State == "observed" && dialog != null) _createRule.IsEnabled = reviewed;
            _accept.Content = L.T(dialog == null ? "failure.action.acceptOnce" : "failure.action.allowOnce");
            HistoryChrome.Guide(_rework, L.T("failure.tip.rework"));
            HistoryChrome.Guide(_abandon, L.T("failure.tip.abandon"));
            HistoryChrome.Guide(_accept, L.T(dialog != null ? "security.failure.dialogOnceWarning" : warning ? "security.failure.onceWarning" : "security.failure.warningOnly"));
            HistoryChrome.Guide(_createRule, L.T(dialog != null ? "security.failure.dialogRuleWarning" : warning ? "security.failure.ruleWarning" : "security.failure.warningOnly"));
            HistoryChrome.Guide(_native, L.T("security.failure.nativeWarning"));
            _decisionHint.Text = _incident == null ? L.T("failure.detail.prompt") : "";
            _decisionHint.Visibility = string.IsNullOrEmpty(_decisionHint.Text) ? Visibility.Collapsed : Visibility.Visible;
            _dialogConsent.Visibility = dialog == null ? Visibility.Collapsed : Visibility.Visible;
            _dialogConsent.Content = dialog == null ? "" : L.T("security.failure.dialogChoice", ("semantic", DialogSemantic(dialog)));
            _native.Visibility = string.IsNullOrWhiteSpace(_incident?.NativeAction) ? Visibility.Collapsed : Visibility.Visible;
            var nativeContext = _incident == null ? null : FailureControl.GetUiContext(_incident.ModelScopeKey);
            var nativeRevision = nativeContext?.IsAvailable == true && nativeContext.ModelScopeKey == _incident?.ModelScopeKey
                && nativeContext.RevitYear == _incident.RevitYear ? nativeContext.DocumentRevision : _incident?.DocumentRevision;
            _native.IsEnabled = reviewed && active && _incident.Complete && !string.IsNullOrWhiteSpace(_incident.NativeAction)
                && !string.IsNullOrWhiteSpace(_incident.NativeContextHash) && !string.IsNullOrWhiteSpace(_incident.NativeContextDescription)
                && !string.IsNullOrWhiteSpace(_incident.RequestHash) && !string.IsNullOrWhiteSpace(nativeRevision);
            var rule = (_rules.SelectedItem as RuleRow)?.Rule;
            _revoke.IsEnabled = rule?.Enabled == true && context?.IsAvailable == true && context.ModelScopeKey == rule.ModelScopeKey;
            PublishShellStatus();
        }

        private void ApplyLocalization()
        {
            foreach (var apply in _localizers) apply();
            foreach (ComboBoxItem item in _states.Items)
                item.Content = (string)item.Tag == "" ? L.T("failure.filter.all") : L.T("failure.state." + item.Tag);
            if (_grid != null) _grid.Items.Refresh();
            _models.Items.Refresh();
            _rules.Items.Refresh();
            if (_warningCards != null) RenderDetails();
            if (_accept != null) UpdateActions();
        }

        private void CopyDetails()
        {
            if (_incident == null) return;
            try
            {
                Clipboard.SetText(DetailsText() + "\n\n" + FailureJson.Serialize(_incident));
                SetStatus(L.T("failure.copied"), Brushes.DimGray);
            }
            catch (Exception ex) { ShowFailure(ex.Message); }
        }

        private void ShowDecisionFailure(Exception error)
        {
            SetStatus(error.Message.StartsWith("security.failure.", StringComparison.Ordinal)
                ? L.T(error.Message) : L.T("security.failure.saveFailed", ("error", error.Message)), Brushes.Firebrick);
        }

        private void ShowFailure(string error)
        {
            SetStatus(L.T("security.failure.saveFailed", ("error", error)), Brushes.Firebrick);
        }

        internal string ShellStatusText { get; private set; } = "";
        internal Brush ShellStatusBrush { get; private set; } = Brushes.DimGray;
        internal event EventHandler ShellStatusChanged;

        private void SetStatus(string text, Brush foreground)
        {
            _status.Text = text ?? "";
            _status.Foreground = foreground ?? Brushes.DimGray;
            PublishShellStatus();
        }

        private void PublishShellStatus()
        {
            var banner = _status.Text ?? "";
            var hint = _decisionHint.Visibility == Visibility.Visible ? (_decisionHint.Text ?? "") : "";
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(banner)) parts.Add(banner.Trim());
            if (!string.IsNullOrWhiteSpace(hint)) parts.Add(hint.Trim());
            ShellStatusText = string.Join(" · ", parts);
            ShellStatusBrush = ReferenceEquals(_status.Foreground, Brushes.Firebrick)
                ? Brushes.Firebrick
                : !string.IsNullOrWhiteSpace(hint) ? DecisionHintBrush : (_status.Foreground ?? Brushes.DimGray);
            ShellStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string SoftWrap(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 24) return text;
            var sb = new System.Text.StringBuilder(text.Length + 8);
            var run = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                sb.Append(c);
                if (char.IsWhiteSpace(c)) { run = 0; continue; }
                run++;
                if (run >= 24 || c == '_' || c == '/' || c == '\\' || c == '.' || c == ',' || c == ';' || c == ':' || c == '-' || c == '|')
                {
                    sb.Append('\u200B');
                    run = 0;
                }
            }
            return sb.ToString();
        }

        private Button DecisionButton(string key, string action)
        {
            var button = Button(key, (s, e) => RecordDecision(action));
            _decisionButtons.Add(button); return button;
        }

        private Button IconButton(string key, string glyph, RoutedEventHandler click)
        {
            var button = new Button { Content = glyph, Style = (Style)BimwrightStyles.Dictionary[BimwrightStyles.IconButtonKey],
                VerticalAlignment = VerticalAlignment.Center };
            _localizers.Add(() => {
                var label = L.T(key);
                System.Windows.Automation.AutomationProperties.SetName(button, label);
                HistoryChrome.Guide(button, label);
            });
            if (click != null) button.Click += click;
            return button;
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

        private void Column(string key, string property, double star, double min, Style style)
        {
            var column = new DataGridTextColumn
            {
                Binding = new Binding(property),
                Width = new DataGridLength(star, DataGridLengthUnitType.Star),
                MinWidth = min,
                ElementStyle = style
            };
            _localizers.Add(() => column.Header = L.T(key));
            _grid.Columns.Add(column);
        }

        private void ShowFacts(params string[] fields)
        {
            _facts.Children.Clear();
            _facts.RowDefinitions.Clear();
            if (fields.Length == 1)
            {
                _facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var note = new TextBlock { Text = fields[0], TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush };
                Grid.SetColumnSpan(note, 4);
                _facts.Children.Add(note);
                return;
            }
            var row = 0;
            for (var i = 0; i + 1 < fields.Length; i += 2)
            {
                var col = ((i / 2) % 2) * 2;
                if (col == 0)
                {
                    _facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    row = _facts.RowDefinitions.Count - 1;
                }
                var shown = string.IsNullOrWhiteSpace(fields[i + 1]) ? "—" : fields[i + 1];
                var label = new TextBlock { Text = fields[i], Foreground = MutedBrush, Margin = new Thickness(col == 0 ? 0 : 16, 1, 6, 1) };
                var value = new TextBlock { Text = shown, TextTrimming = TextTrimming.CharacterEllipsis };
                if (shown != "—") HistoryChrome.Guide(value, shown);
                Grid.SetRow(label, row); Grid.SetColumn(label, col);
                Grid.SetRow(value, row); Grid.SetColumn(value, col + 1);
                _facts.Children.Add(label); _facts.Children.Add(value);
            }
        }

        private void UpdateWarningFades()
        {
            if (_warningScroll == null) return;
            var canScroll = _warningScroll.ScrollableHeight > 1;
            HistoryChrome.FadeTo(_warningFadeTop, canScroll && _warningScroll.VerticalOffset > 1);
            HistoryChrome.FadeTo(_warningFadeBottom, canScroll && _warningScroll.VerticalOffset < _warningScroll.ScrollableHeight - 1);
        }

        private static Border Notice(string text, bool alert)
        {
            return new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 8, 10, 8),
                Background = alert ? WarningBackground : NoticeBackground,
                BorderBrush = alert ? WarningBorderBrush : NoticeBorder,
                BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
            };
        }

        private Border WarningCard(FailureMessage message, int index, int total)
        {
            var severity = string.IsNullOrWhiteSpace(message.Severity) ? Unknown() : message.Severity;
            Brush background = NoticeBackground, border = NoticeBorder, foreground = MutedBrush;
            if (severity == "Error") { background = ErrorBackground; border = ErrorBorder; foreground = ErrorBrush; }
            else if (severity == "Warning") { background = WarningBackground; border = WarningBorderBrush; foreground = WarningBrush; }
            else if (severity == "TaskDialog") { background = DialogBackground; border = DialogBorder; foreground = DialogBrush; }
            var ids = (message.FailingElementIds ?? new List<long>()).Concat(message.AdditionalElementIds ?? new List<long>()).Distinct().ToList();
            var snapshots = message.Snapshots ?? new List<FailureElementSnapshot>();
            var stack = new StackPanel();
            var head = new DockPanel { LastChildFill = true };
            var copy = new Button
            {
                Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = "\uE8C8", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            HistoryChrome.Guide(copy, L.T("failure.copy.warning"));
            copy.Click += (s, e) => CopyWarning(message, index, total);
            DockPanel.SetDock(copy, Dock.Right);
            head.Children.Add(copy);
            var indexText = new TextBlock
            {
                Text = index.ToString("n0", CultureInfo.CurrentCulture) + " / " + total.ToString("n0", CultureInfo.CurrentCulture),
                Foreground = MutedBrush, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(indexText, Dock.Right);
            head.Children.Add(indexText);
            var badge = new TextBlock { Text = severity, FontWeight = FontWeights.SemiBold, Foreground = foreground, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(badge, Dock.Left);
            head.Children.Add(badge);
            head.Children.Add(new TextBlock { Text = message.DefinitionId ?? "", Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock
            {
                Text = SoftWrap(string.IsNullOrWhiteSpace(message.Description) ? Unknown() : message.Description),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = SoftWrap(ids.Count == 0 ? L.T("failure.detail.noElements") : L.T("failure.detail.elements", ("ids", string.Join(", ", ids.Take(8))))),
                TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0)
            });
            if (ids.Count > 8)
                stack.Children.Add(new TextBlock { Text = L.T("failure.detail.moreIds", ("count", ids.Count - 8)), TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush, Margin = new Thickness(0, 2, 0, 0) });
            if (!message.Complete || message.TextTruncated)
                stack.Children.Add(new TextBlock { Text = L.T("failure.detail.incomplete"), TextWrapping = TextWrapping.Wrap, Foreground = ErrorBrush, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var snapshot in snapshots.Take(3))
                stack.Children.Add(new TextBlock
                {
                    Text = SoftWrap(snapshot.ElementId + " · " + snapshot.Category + " · " + snapshot.Type + " · " + snapshot.Level),
                    TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush, Margin = new Thickness(0, 2, 0, 0)
                });
            if (snapshots.Count > 3)
                stack.Children.Add(new TextBlock { Text = L.T("failure.detail.moreSnapshots", ("count", snapshots.Count - 3)), Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            return new Border
            {
                Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 8, 10, 8),
                Background = background, BorderBrush = border, BorderThickness = new Thickness(3, 1, 1, 1),
                CornerRadius = new CornerRadius(4), Child = stack
            };
        }

        private void CopyWarning(FailureMessage message, int index, int total)
        {
            var ids = (message.FailingElementIds ?? new List<long>()).Concat(message.AdditionalElementIds ?? new List<long>()).Distinct().ToList();
            var text = new StringBuilder();
            text.AppendLine(index.ToString("n0", CultureInfo.CurrentCulture) + " / " + total.ToString("n0", CultureInfo.CurrentCulture));
            text.AppendLine(string.IsNullOrWhiteSpace(message.Severity) ? Unknown() : message.Severity);
            if (!string.IsNullOrWhiteSpace(message.DefinitionId)) text.AppendLine(message.DefinitionId);
            text.AppendLine(string.IsNullOrWhiteSpace(message.Description) ? Unknown() : message.Description);
            text.AppendLine(ids.Count == 0 ? L.T("failure.detail.noElements") : L.T("failure.detail.elements", ("ids", string.Join(", ", ids))));
            if (!message.Complete || message.TextTruncated) text.AppendLine(L.T("failure.detail.incomplete"));
            foreach (var snapshot in message.Snapshots ?? new List<FailureElementSnapshot>())
                text.AppendLine(snapshot.ElementId + " · " + snapshot.Category + " · " + snapshot.Type + " · " + snapshot.Level
                    + " · existsAfterRollback=" + (snapshot.ExistsAfterRollback?.ToString() ?? Unknown()));
            try { Clipboard.SetText(text.ToString().TrimEnd()); SetStatus(L.T("failure.copied"), Brushes.DimGray); }
            catch (Exception ex) { ShowFailure(ex.Message); }
        }

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static void Add(Grid grid, UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
        private static string Unknown() => L.T("failure.detail.unknown");
        private static string ActionLabel(string action) => L.T("security.failure." +
            (action == "accept_once" ? "acceptOnce" : action == "create_rule" ? "createRule"
                : action == "native_grant" ? "nativeGrant" : action));

        private static DataTemplate ModelTemplate()
        {
            var row = new FrameworkElementFactory(typeof(DockPanel));
            row.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Description"));
            var count = new FrameworkElementFactory(typeof(TextBlock));
            count.SetValue(DockPanel.DockProperty, Dock.Right);
            count.SetValue(TextBlock.ForegroundProperty, Brushes.Firebrick);
            count.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            count.SetBinding(TextBlock.TextProperty, new Binding("Count"));
            row.AppendChild(count);
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            label.SetBinding(TextBlock.TextProperty, new Binding("Prefix"));
            label.SetBinding(TextBlock.FontWeightProperty, new Binding("LabelWeight"));
            row.AppendChild(label);
            return new DataTemplate(typeof(ModelRow)) { VisualTree = row };
        }

        private static Style ModelSearchStyle()
        {
            var style = FailureModelPicker.CreateItemStyle();
            var hidden = new DataTrigger { Binding = new Binding("MatchesSearch"), Value = false };
            hidden.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            hidden.Setters.Add(new Setter(UIElement.IsEnabledProperty, false));
            style.Triggers.Add(hidden);
            return style;
        }

        private sealed class ModelRow : INotifyPropertyChanged
        {
            private readonly FailureIssueModel _model;
            private bool _isOpen;
            public FailureIssueModel Source => _model;
            public string ModelScopeKey => _model.ModelScopeKey;
            public int RevitYear => _model.RevitYear;
            public bool IsCurrent { get; private set; }
            public double SearchScore { get; private set; } = 1;
            public bool MatchesSearch => SearchScore >= 0.8;
            public bool IsOpen
            {
                get => _isOpen;
                set
                {
                    if (_isOpen == value) return;
                    _isOpen = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LabelWeight)));
                }
            }
            public FontWeight LabelWeight => IsOpen ? FontWeights.Bold : FontWeights.Normal;
            public string Title => string.IsNullOrWhiteSpace(_model.Title) ? Unknown() + " (" + ModelScopeKey.Substring(0, Math.Min(8, ModelScopeKey.Length)) + ")" : _model.Title;
            public string Label => (RevitYear == 0 ? "R?" : "R" + (RevitYear % 100).ToString("D2", CultureInfo.InvariantCulture))
                + " - " + (_model.IsFamilyDocument.HasValue
                    ? L.T(_model.IsFamilyDocument.Value ? "failure.modelKind.family" : "failure.modelKind.project") : Unknown()) + " - " + Title;
            public string Prefix => Label;
            public string Count => _model.IssueCount.ToString("n0", CultureInfo.CurrentCulture);
            public string Description => (IsCurrent ? L.T("failure.activeModel", ("title", Title)) + "\n" : "") + Prefix + "\n" + Count + "\n" + ModelScopeKey;
            public event PropertyChangedEventHandler PropertyChanged;
            internal ModelRow(FailureIssueModel model, FailureIssuesContext context, ICollection<string> openScopes)
            {
                _model = model;
                UpdateContext(context, openScopes);
            }

            internal void UpdateContext(FailureIssuesContext context, ICollection<string> openScopes)
            {
                IsOpen = RevitYear == context?.RevitYear && FailureIssuesPresentation.IssueModelIsOpen(ModelScopeKey, openScopes);
                var current = context?.IsAvailable == true && ModelScopeKey == context.ModelScopeKey && RevitYear == context.RevitYear;
                if (IsCurrent == current) return;
                IsCurrent = current;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Prefix)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
            }

            internal void UpdateSearch(string query)
            {
                var score = FailureIssuesPresentation.ModelNameSearchScore(_model.Title, query);
                if (SearchScore == score) return;
                SearchScore = score;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchScore)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchesSearch)));
            }
        }

        private sealed class IssueRow
        {
            internal FailureIncident Incident { get; }
            public int Number { get; set; }
            public string Date => Incident.CreatedUtc.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
            public string Clock => Incident.CreatedUtc.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            public string Tool { get; }
            public int Count => Incident.MessageCount;
            internal IssueRow(FailureIncident incident, FailureOperation operation)
            { Incident = incident; Tool = operation?.Tool ?? Unknown(); }
        }

        private sealed class RuleRow
        {
            internal FailureRule Rule { get; }
            public string Label => RuleText(Rule);
            internal RuleRow(FailureRule rule) { Rule = rule; }
        }
    }
}
