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
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Presentation.Issues;
using RvtMcp.Plugin.Infrastructure.UI;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views
{
    /// <summary>Modeless local ledger review. Native facts arrive through API-thread snapshots.</summary>
    internal sealed class FailureIssuesView : UserControl, IDisposable
    {
        // Bounded read on the Revit UI thread; the status line says when older issues are left out.
        private readonly IssuesViewModel _vm;
        private bool _observing, _disposed;
        internal IssuesViewModel ViewModel => _vm;
        private readonly FailureModelPicker _models = new FailureModelPicker { Height = 28, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        private TextBox _modelSearch => _models.SearchBox;
        private TextBlock _modelSearchHint => _models.SearchHint;
        private TextBlock _modelSearchEmpty => _models.EmptyHint;
        private readonly ComboBox _states = new ComboBox { Height = 28, MinWidth = 130, VerticalContentAlignment = VerticalAlignment.Center };
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
        private FailureIncident _incident => _vm.Incident;
        private FailureOperation _operation => _vm.Operation;
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

        internal FailureIssuesView(IIssuesRuntime runtime = null, IIssuesDialogs dialogs = null)
        {
            _vm = new IssuesViewModel(runtime ?? new IssuesRuntime(), dialogs ?? new IssuesDialogs(() => Window.GetWindow(this)));
            DataContext = _vm;
            _models.ItemTemplate = ModelTemplate();
            _models.IsTextSearchEnabled = false;
            _models.ItemContainerStyle = ModelSearchStyle();
            _models.DropDownOpened += (s, e) => _vm.ApplyOpenEmphasis();
            _modelSearch.TextChanged += (s, e) => {
                _modelSearchHint.Visibility = string.IsNullOrEmpty(_modelSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
                _vm.ApplyOpenEmphasis();
            };
            var root = new Grid { Margin = new Thickness(8, 6, 8, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });

            var toolbar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var reset = Button("failure.reset", null); reset.Command = _vm.ResetCommand; actions.Children.Add(reset);
            var refresh = Button("failure.refresh", null); refresh.Command = _vm.RefreshCommand; actions.Children.Add(refresh);
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
            _copyDetails = IconButton("failure.copy", "\uE8C8", null);
            _copyDetails.Command = _vm.CopyCommand;
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
            _revoke = Button("security.failure.revokeRule", null);
            _revoke.Command = _vm.RevokeCommand;
            _revoke.HorizontalAlignment = HorizontalAlignment.Left;
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

            Bind(_models, ItemsControl.ItemsSourceProperty, nameof(IssuesViewModel.Models));
            Bind(_models, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, nameof(IssuesViewModel.SelectedModel), BindingMode.TwoWay);
            Bind(_modelSearch, TextBox.TextProperty, nameof(IssuesViewModel.SearchText), BindingMode.TwoWay);
            _states.SelectedValuePath = "Tag";
            Bind(_states, System.Windows.Controls.Primitives.Selector.SelectedValueProperty, nameof(IssuesViewModel.State), BindingMode.TwoWay);
            Bind(_grid, ItemsControl.ItemsSourceProperty, nameof(IssuesViewModel.Rows));
            Bind(_grid, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, nameof(IssuesViewModel.SelectedIssue), BindingMode.TwoWay);
            Bind(_rules, ItemsControl.ItemsSourceProperty, nameof(IssuesViewModel.Rules));
            Bind(_rules, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, nameof(IssuesViewModel.SelectedRule), BindingMode.TwoWay);
            Bind(_reason, TextBox.TextProperty, nameof(IssuesViewModel.Reason), BindingMode.TwoWay);
            Bind(_verified, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(IssuesViewModel.VerifiedModel), BindingMode.TwoWay);
            Bind(_dialogConsent, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(IssuesViewModel.DialogConsent), BindingMode.TwoWay);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnVisibleChanged;
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
            return panel;
        }

        internal void Refresh() => _vm.Refresh();
        private void Bind(FrameworkElement target, DependencyProperty property, string path, BindingMode mode = BindingMode.OneWay) =>
            target.SetBinding(property, new Binding(path) { Source = _vm, Mode = mode, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });

        private void ShowRules(bool any)
        {
            _rules.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _rulesEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            _revoke.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _rulesPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RenderDetails()
        {
            _warningCards.Children.Clear();
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

        private void UpdateActions() => _vm.RefreshActions();
        private void RenderActions()
        {
            var dialog = _vm.HasDialog;
            var warning = _vm.IsAcceptableWarning;
            _accept.Content = L.T(dialog ? "failure.action.allowOnce" : "failure.action.acceptOnce");
            HistoryChrome.Guide(_rework, L.T("failure.tip.rework"));
            HistoryChrome.Guide(_abandon, L.T("failure.tip.abandon"));
            HistoryChrome.Guide(_accept, L.T(dialog ? "security.failure.dialogOnceWarning" : warning ? "security.failure.onceWarning" : "security.failure.warningOnly"));
            HistoryChrome.Guide(_createRule, L.T(dialog ? "security.failure.dialogRuleWarning" : warning ? "security.failure.ruleWarning" : "security.failure.warningOnly"));
            HistoryChrome.Guide(_native, L.T("security.failure.nativeWarning"));
            _decisionHint.Text = _vm.DecisionHint;
            _decisionHint.Visibility = string.IsNullOrEmpty(_decisionHint.Text) ? Visibility.Collapsed : Visibility.Visible;
            _dialogConsent.Visibility = dialog ? Visibility.Visible : Visibility.Collapsed;
            _dialogConsent.Content = dialog ? L.T("security.failure.dialogChoice", ("semantic", IssuesText.DialogSemantic(FailureIssuesPresentation.DialogAdapter(_incident)))) : "";
            _native.Visibility = _vm.HasNativeAction ? Visibility.Visible : Visibility.Collapsed;
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
            if (_accept != null) RenderActions();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_disposed) return;
            if (!_observing) { _observing = true; _vm.PropertyChanged += OnViewModelChanged; }
            ApplyLocalization();
            if (IsVisible) _vm.Activate();
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _vm.Deactivate();
            if (_observing) { _observing = false; _vm.PropertyChanged -= OnViewModelChanged; }
        }
        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_disposed) return;
            if (IsVisible && IsLoaded) _vm.Activate(); else _vm.Deactivate();
        }
        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            if (e.PropertyName == nameof(IssuesViewModel.Incident)) RenderDetails();
            else if (e.PropertyName == nameof(IssuesViewModel.Rules)) ShowRules(_vm.Rules.Count > 0);
            else if (e.PropertyName == nameof(IssuesViewModel.ActionStateVersion)) RenderActions();
            else if (e.PropertyName == nameof(IssuesViewModel.LanguageVersion)) ApplyLocalization();
            else if (e.PropertyName == nameof(IssuesViewModel.SearchEmpty)) _modelSearchEmpty.Visibility = _vm.SearchEmpty ? Visibility.Visible : Visibility.Collapsed;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            OnUnloaded(this, null);
            IsVisibleChanged -= OnVisibleChanged;
            foreach (var control in new FrameworkElement[] { _models, _modelSearch, _states, _grid, _rules, _reason, _verified, _dialogConsent }) BindingOperations.ClearAllBindings(control);
            foreach (var button in _decisionButtons) button.Command = null;
            _revoke.Command = _copyDetails.Command = null;
            DataContext = null;
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
            var button = Button(key, null);
            button.Command = _vm.DecisionCommand(action);
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
            if (click != null) button.Click += click; return button;
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
            copy.Click += (s, e) => _vm.CopyWarning(message, index, total);
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

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static void Add(Grid grid, UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
        private static string Unknown() => L.T("failure.detail.unknown");
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
            label.SetBinding(TextBlock.FontWeightProperty, new Binding("IsOpen") { Converter = new OpenWeightConverter() });
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

        private sealed class OpenWeightConverter : IValueConverter
        {
            public object Convert(object value, Type type, object parameter, CultureInfo culture) => Equals(value, true) ? FontWeights.Bold : FontWeights.Normal;
            public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
        }
    }
}
