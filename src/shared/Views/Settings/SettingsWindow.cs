using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;
using RvtMcp.ToolCatalog;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>
    /// Modeless, owner-bound Settings shell. Controls are built in code so all six
    /// plugin shells share the same layout without loose XAML files. The window only
    /// presents <see cref="ISettingsPresentation"/>; persistence stays in the adapter.
    /// </summary>
    public sealed partial class SettingsWindow : Window
    {
        private static readonly int[] ToastIdleOptions = { 10, 20, 30, 60 };

        private readonly ISettingsPresentation _viewModel;
        private readonly ISettingsToolsPresentation _toolsViewModel;
        private readonly AboutInfo _aboutInfo;
        private readonly HashSet<FrameworkElement> _boundControls = new HashSet<FrameworkElement>();
        private readonly TabControl _tabs;
        private readonly Dictionary<SettingsTab, TabItem> _tabItems = new Dictionary<SettingsTab, TabItem>();
        // Re-run on every language change so all localized chrome follows L.Changed.
        private readonly List<Action> _localizedText = new List<Action>();
        // Inline error line under each setting row, keyed by config key.
        private readonly Dictionary<string, TextBlock> _fieldErrors = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        private bool _closing;
        private string _footerError;
        private DispatcherTimer _copiedTimer;

        private Border _statePill;
        private Ellipse _stateDot;
        private TextBlock _stateText;
        private Button _restartListener;
        private CheckBox _listenerEnabled;
        private TextBlock _transportText;
        private Button _copyPort;
        private ComboBox _language;
        private TextBlock _languageCaption;
        private ComboBox _warningPolicy;
        private TextBlock _warningPolicyCaption;
        private CheckBox _cacheSendCodeBodies;
        private CheckBox _persistSendCodeBodies;
        private ComboBox _journalHours;
        private CheckBox _toastEnabled;
        private CheckBox _showBranding;
        private ComboBox _toastHorizontal;
        private ComboBox _toastVertical;
        private CheckBox _toastDrag;
        private Button _toastReset;
        private ComboBox _toastIdle;
        private DataGrid _toolsGrid;
        private TextBlock _toolsCounts;
        private TextBlock _toolsStatus;
        private TextBlock _toolSearchPlaceholder;
        private TextBox _toolSearch;
        private Ellipse _footerDot;
        private TextBlock _footerText;
        private Button _apply;
        private Button _discard;

        public SettingsWindow(ISettingsPresentation viewModel, ISettingsToolsPresentation toolsViewModel, SettingsTab initialTab = SettingsTab.General)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _toolsViewModel = toolsViewModel ?? throw new ArgumentNullException(nameof(toolsViewModel));
            _aboutInfo = AboutInfoProvider.Create(Assembly.GetExecutingAssembly(), _viewModel.RevitYear);
            DataContext = _viewModel;
            Width = 660;
            Height = 640;
            MinWidth = 594;
            MinHeight = 480;
            MaxWidth = Width * 1.1;
            MaxHeight = Height * 1.1;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            Foreground = SettingsStyles.Text;
            Background = SettingsStyles.Background;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Resources.MergedDictionaries.Add(SettingsStyles.Create());
            ScrollBarFadeBehavior.SetIsEnabled(this, true);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(BuildHeader());
            _tabs = new TabControl { Margin = new Thickness(20, 6, 20, 0) };
            AddTab(SettingsTab.General, BuildGeneral(), scroll: true);
            AddTab(SettingsTab.Toast, BuildToast(), scroll: true);
            // The grid scrolls itself; an outer ScrollViewer would defeat row virtualization.
            AddTab(SettingsTab.Tools, BuildTools(), scroll: false);
            AddTab(SettingsTab.About, BuildAbout(), scroll: true);
            Grid.SetRow(_tabs, 1);
            root.Children.Add(_tabs);
            var footer = BuildFooter();
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;
            BindControls();

            Loaded += (_, __) => SelectTab(initialTab);
            Closing += OnClosing;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.LanguageChanged += OnViewModelLanguageChanged;
            _toolsViewModel.PropertyChanged += OnToolsPropertyChanged;
            _tabs.SelectionChanged += OnTabsSelectionChanged;
            SelectTab(initialTab);
            UpdateLocalizedChrome();
            UpdateDynamicText();
        }

        private void BindControls()
        {
            Bind(_toolSearch, TextBox.TextProperty, nameof(ISettingsToolsPresentation.SearchText), BindingMode.TwoWay, source: _toolsViewModel);
            Bind(_toolsGrid, ItemsControl.ItemsSourceProperty, nameof(ISettingsToolsPresentation.Rows), source: _toolsViewModel);
            Bind(_toolsGrid, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, nameof(ISettingsToolsPresentation.SelectedTool), BindingMode.TwoWay, source: _toolsViewModel);
            Bind(_listenerEnabled, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(ISettingsPresentation.IsListenerRunning), BindingMode.TwoWay);
            Bind(_toastEnabled, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(ISettingsPresentation.ToastEnabled), BindingMode.TwoWay);
            Bind(_showBranding, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(ISettingsPresentation.ShowBranding), BindingMode.TwoWay);
            Bind(_cacheSendCodeBodies, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(ISettingsPresentation.CacheSendCodeBodies), BindingMode.TwoWay);
            Bind(_persistSendCodeBodies, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(ISettingsPresentation.PersistSendCodeBodies), BindingMode.TwoWay);
            Bind(_toastDrag, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(ISettingsPresentation.ToastDragEnabled), BindingMode.TwoWay);
            _language.SelectedValuePath = "Tag";
            _warningPolicy.SelectedValuePath = "Tag";
            Bind(_language, System.Windows.Controls.Primitives.Selector.SelectedValueProperty, nameof(ISettingsPresentation.SelectedLanguage), BindingMode.TwoWay, new SelectionValueConverter());
            Bind(_warningPolicy, System.Windows.Controls.Primitives.Selector.SelectedValueProperty, nameof(ISettingsPresentation.WarningPolicy), BindingMode.TwoWay, new SelectionValueConverter());
            Bind(_toastIdle, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, nameof(ISettingsPresentation.ToastIdleSeconds), BindingMode.TwoWay, new SelectionValueConverter());
            Bind(_journalHours, System.Windows.Controls.Primitives.Selector.SelectedItemProperty, nameof(ISettingsPresentation.PersistSendCodeBodiesHours), BindingMode.TwoWay, new SelectionValueConverter());
            Bind(_toastHorizontal, System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, nameof(ISettingsPresentation.ToastRight), BindingMode.TwoWay, new CornerIndexConverter());
            Bind(_toastVertical, System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, nameof(ISettingsPresentation.ToastBottom), BindingMode.TwoWay, new CornerIndexConverter());
            Bind(_language, IsEnabledProperty, nameof(ISettingsPresentation.IsLanguageWriteDisabled), BindingMode.OneWay, new InverseBooleanConverter());
            Bind(_warningPolicy, IsEnabledProperty, nameof(ISettingsPresentation.IsWarningPolicyWriteDisabled), BindingMode.OneWay, new InverseBooleanConverter());
            foreach (var control in new FrameworkElement[] { _showBranding, _toastHorizontal, _toastVertical, _toastDrag, _toastIdle })
                Bind(control, IsEnabledProperty, nameof(ISettingsPresentation.ToastEnabled));
            Bind(_apply, Button.CommandProperty, nameof(ISettingsPresentation.ApplyCommand));
            Bind(_discard, Button.CommandProperty, nameof(ISettingsPresentation.DiscardCommand));
            Bind(_restartListener, Button.CommandProperty, nameof(ISettingsPresentation.RestartListenerCommand));
            Bind(_copyPort, Button.CommandProperty, nameof(ISettingsPresentation.CopyPortCommand));
            Bind(_toastReset, Button.CommandProperty, nameof(ISettingsPresentation.ResetToastPositionCommand));
        }

        private void Bind(FrameworkElement target, DependencyProperty property, string path,
            BindingMode mode = BindingMode.OneWay, IValueConverter converter = null, object source = null)
        {
            _boundControls.Add(target);
            target.SetBinding(property, new Binding(path) { Source = source ?? _viewModel, Mode = mode,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, Converter = converter });
        }

        private sealed class SelectionValueConverter : IValueConverter
        {
            public object Convert(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => value;
            public object ConvertBack(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => value ?? Binding.DoNothing;
        }
        private sealed class CornerIndexConverter : IValueConverter
        {
            public object Convert(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => Equals(value, true) ? 1 : 0;
            public object ConvertBack(object value, Type type, object parameter, System.Globalization.CultureInfo culture) =>
                value is int index && (index == 0 || index == 1) ? (object)(index == 1) : Binding.DoNothing;
        }
        private sealed class InverseBooleanConverter : IValueConverter
        {
            public object Convert(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => !Equals(value, true);
            public object ConvertBack(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
        }

        public void SelectTab(SettingsTab tab)
        {
            TabItem item;
            if (_tabItems.TryGetValue(tab, out item))
                _tabs.SelectedItem = item;
        }

        public void FocusLanguage()
        {
            SelectTab(SettingsTab.General);
            if (_language == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _language.Focus();
                _language.BringIntoView();
            }));
        }

        /// <summary>Used by App.OnShutdown so Revit shutdown never opens a discard prompt.</summary>
        public void CloseForShutdown()
        {
            _closing = true;
            Close();
        }

        private void AddTab(SettingsTab key, UIElement content, bool scroll)
        {
            var item = new TabItem
            {
                // Scrolling pages borrow the right gutter so the scrollbar never touches the cards.
                Content = scroll
                    ? new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Margin = new Thickness(0, 0, -16, 0) }
                    : content
            };
            _tabItems[key] = item;
            _tabs.Items.Add(item);
        }

        private UIElement BuildHeader()
        {
            var subtitle = new Run { FontWeight = FontWeights.Normal, Foreground = SettingsStyles.Secondary };
            _localizedText.Add(() => subtitle.Text = " " + SettingsText.Text("settings.header.subtitle", "Settings"));
            var header = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(20, 14, 20, 0), ToolTip = BrandAssets.ProductTag };
            header.Inlines.Add(new Run(BrandAssets.WordmarkLeft) { Foreground = SettingsStyles.BrandBim });
            header.Inlines.Add(new Run(BrandAssets.WordmarkRight) { Foreground = SettingsStyles.BrandWright });
            header.Inlines.Add(new Run("  |  ") { FontWeight = FontWeights.Normal, Foreground = SettingsStyles.NeutralDot });
            header.Inlines.Add(new Run("RVT-MCP"));
            header.Inlines.Add(subtitle);
            return header;
        }

        private UIElement BuildGeneral()
        {
            var page = Page();

            page.Children.Add(SectionTitle("settings.general.connection", "Connection"));
            _stateDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            _stateText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var pill = new StackPanel { Orientation = Orientation.Horizontal };
            pill.Children.Add(_stateDot);
            pill.Children.Add(_stateText);
            _statePill = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 4), Child = pill };
            // Restart = ribbon Off then On: the listener comes back with a new port/pipe and token.
            _restartListener = new Button { Style = (Style)FindResource("Icon"), Content = "\uE72C", Margin = new Thickness(8, 0, 4, 0) };
            ToolTipService.SetShowOnDisabled(_restartListener, true);
            _localizedText.Add(() =>
            {
                var text = SettingsText.Text("settings.general.restart", "Restart the listener with a new port");
                _restartListener.ToolTip = text;
                AutomationProperties.SetName(_restartListener, text);
            });
            _listenerEnabled = new CheckBox { Margin = new Thickness(4, 0, 0, 0) };
            _localizedText.Add(() =>
            {
                var text = SettingsText.Text("settings.general.listener", "MCP listener");
                _listenerEnabled.ToolTip = text;
                AutomationProperties.SetName(_listenerEnabled, text);
            });
            var state = new StackPanel { Orientation = Orientation.Horizontal };
            state.Children.Add(_statePill);
            state.Children.Add(_restartListener);
            state.Children.Add(_listenerEnabled);

            _transportText = RowLabelText();
            _copyPort = new Button { MinWidth = 68 };
            LocalizedButton(_copyPort, "settings.general.copy", "Copy");
            _copyPort.Click += OnCopyPort;
            ToolTipService.SetShowOnDisabled(_copyPort, true);
            page.Children.Add(Card(
                Row(RowLabel("settings.general.state", "State"), null, state, "listener"),
                Row(_transportText, null, _copyPort)));

            // Language is intentionally invariant English per the Settings contract.
            var languageTitle = SectionTitleText();
            languageTitle.Text = InvariantLanguageText.Heading;
            page.Children.Add(languageTitle);
            _language = new ComboBox { Width = 220 };
            _language.Items.Add(new ComboBoxItem { Content = InvariantLanguageText.AutoOption, Tag = LocaleResolver.Auto });
            foreach (var locale in LocaleResolver.SupportedLocales)
                _language.Items.Add(new ComboBoxItem { Content = LocaleResolver.NativeName(locale), Tag = locale });
            AutomationProperties.SetName(_language, InvariantLanguageText.AutomationName);
            AutomationProperties.SetHelpText(_language, InvariantLanguageText.AutomationHelp);
            var languageLabel = RowLabelText();
            languageLabel.Text = InvariantLanguageText.RowLabel;
            _languageCaption = RowCaptionText();
            page.Children.Add(Card(Row(languageLabel, _languageCaption, _language, "uiLanguage")));

            page.Children.Add(SectionTitle("settings.general.safety", "Safety"));
            var policyLabel = RowLabel("security.warningPolicy.label", "Warning policy");
            _warningPolicy = new ComboBox { Width = 220 };
            foreach (var mode in new[] { RvtMcp.Failures.WarningPolicyModes.Rollback, RvtMcp.Failures.WarningPolicyModes.AcceptAndLog })
            {
                var option = new ComboBoxItem { Tag = mode };
                _localizedText.Add(() => option.Content = SettingsText.Text(mode == RvtMcp.Failures.WarningPolicyModes.Rollback
                    ? "security.warningPolicy.rollback" : "security.warningPolicy.acceptAndLog",
                    mode == RvtMcp.Failures.WarningPolicyModes.Rollback ? "Rollback (default)" : "Accept warnings and log"));
                _warningPolicy.Items.Add(option);
            }
            _localizedText.Add(() => AutomationProperties.SetName(_warningPolicy, policyLabel.Text));
            _warningPolicyCaption = RowCaptionText();
            page.Children.Add(Card(Row(policyLabel, _warningPolicyCaption, _warningPolicy, "warningPolicy")));

            page.Children.Add(SectionTitle("settings.general.privacy", "Privacy"));
            var cacheLabel = RowLabel("settings.general.cacheBodies", "Cache send_code bodies");
            _cacheSendCodeBodies = Switch(cacheLabel);
            var journalLabel = RowLabel("settings.general.keepJournal", "Keep send_code journal");
            _persistSendCodeBodies = Switch(journalLabel);
            // The duration sits beside the journal switch it belongs to; its label names/tooltips it.
            _journalHours = new ComboBox { Width = 84, ItemsSource = new[] { 1, 4, 8, 12, 24, 48, 168, 336 },
                IsReadOnly = true, IsTextSearchEnabled = false, Margin = new Thickness(0, 0, 12, 0) };
            var durationText = new FrameworkElementFactory(typeof(TextBlock));
            durationText.SetBinding(TextBlock.TextProperty, new Binding { Converter = new JournalHoursConverter() });
            _journalHours.ItemTemplate = new DataTemplate { VisualTree = durationText };
            _localizedText.Add(() =>
            {
                var hoursText = SettingsText.Text("settings.general.journalDuration", "Journal duration (hours)");
                _journalHours.ToolTip = hoursText;
                AutomationProperties.SetName(_journalHours, hoursText);
            });
            var journalControls = new StackPanel { Orientation = Orientation.Horizontal };
            journalControls.Children.Add(_journalHours);
            journalControls.Children.Add(_persistSendCodeBodies);
            page.Children.Add(Card(
                Row(cacheLabel,
                    RowCaption("settings.general.cacheBodies.help", "Keeps submitted C# in History for reuse and bake suggestions."),
                    _cacheSendCodeBodies, "cacheSendCodeBodies"),
                Row(journalLabel,
                    RowCaption("settings.general.keepJournal.help", "Writes submitted C# to a local journal until the duration ends."),
                    journalControls, "persistSendCodeBodies", "persistSendCodeBodiesHours")));
            return page;
        }

        private UIElement BuildToast()
        {
            var page = Page();
            page.Children.Add(SectionTitle("settings.toast.heading", "Activity notifications"));
            var description = RowCaption("settings.toast.description", "Control how activity appears while you work.");
            description.Margin = new Thickness(0, -2, 0, 8);
            page.Children.Add(description);

            var enabledLabel = RowLabel("settings.toast.enabled", "Show activity notifications");
            _toastEnabled = Switch(enabledLabel);
            var brandLabel = RowLabel("settings.toast.brand", "Show branding");
            _showBranding = Switch(brandLabel);
            var horizontalLabel = RowLabel("settings.toast.horizontal", "Horizontal alignment");
            _toastHorizontal = PositionChoice(horizontalLabel, "settings.toast.left", "Left", "settings.toast.right", "Right");
            var verticalLabel = RowLabel("settings.toast.vertical", "Vertical alignment");
            _toastVertical = PositionChoice(verticalLabel, "settings.toast.top", "Top", "settings.toast.bottom", "Bottom");
            var dragLabel = RowLabel("settings.toast.drag", "Allow dragging the card");
            _toastDrag = Switch(dragLabel);
            _toastReset = new Button { MinWidth = 120, Padding = new Thickness(10, 3, 10, 3) };
            LocalizedButton(_toastReset, "settings.toast.reset", "Reset position");
            var idleLabel = RowLabel("settings.toast.idle", "Idle duration");
            _toastIdle = new ComboBox { Width = 120, ItemsSource = ToastIdleOptions, ItemStringFormat = "{0} s" };
            NameFromLabel(_toastIdle, idleLabel);
            page.Children.Add(Card(
                Row(enabledLabel, RowCaption("settings.toast.enabled.help", "Takes effect immediately."), _toastEnabled, "enableToast"),
                Row(brandLabel, RowCaption("settings.toast.brand.help", "Appears when you point at the activity card. Applies immediately and is remembered after Revit restarts."), _showBranding, "showBranding"),
                Row(horizontalLabel,
                    RowCaption("settings.toast.position.help", "Applies and saves immediately. Changing a corner clears the saved drag position."),
                    _toastHorizontal, "toastPosition"),
                Row(verticalLabel, null, _toastVertical),
                Row(dragLabel,
                    RowCaption("settings.toast.drag.help", "Drag the title row to move the card. Turning this off keeps the saved position; turning it on restores it."),
                    _toastDrag),
                Row(RowLabel("settings.toast.reset", "Reset position"), null, _toastReset),
                Row(idleLabel,
                    RowCaption("settings.toast.idle.help", "Hides the card when no new results arrive. Hover to keep it open. Applies from the next activity."),
                    _toastIdle, "toastIdleSeconds")));
            return page;
        }

        /// <summary>Two-way choice (first/second) whose captions follow the UI language.</summary>
        private ComboBox PositionChoice(TextBlock label, string firstKey, string firstFallback, string secondKey, string secondFallback)
        {
            var first = new ComboBoxItem();
            var second = new ComboBoxItem();
            var choice = new ComboBox { Width = 120 };
            choice.Items.Add(first);
            choice.Items.Add(second);
            NameFromLabel(choice, label);
            _localizedText.Add(() =>
            {
                first.Content = SettingsText.Text(firstKey, firstFallback);
                second.Content = SettingsText.Text(secondKey, secondFallback);
            });
            return choice;
        }

        private UIElement BuildTools()
        {
            var page = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var summary = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _toolsCounts = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            _toolsStatus = RowCaptionText();
            summary.Children.Add(_toolsCounts);
            summary.Children.Add(_toolsStatus);
            toolbar.Children.Add(summary);
            var refresh = new Button { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            LocalizedButton(refresh, "settings.tools.refresh", "Refresh");
            Bind(refresh, Button.CommandProperty, nameof(ISettingsToolsPresentation.RefreshCommand), source: _toolsViewModel);
            Grid.SetColumn(refresh, 1);
            toolbar.Children.Add(refresh);
            DockPanel.SetDock(toolbar, Dock.Top);
            page.Children.Add(toolbar);
            var search = BuildToolSearch();
            DockPanel.SetDock(search, Dock.Top);
            page.Children.Add(search);

            _toolsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserResizeColumns = false,
                CanUserResizeRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                MinColumnWidth = 50,
            };
            _localizedText.Add(() =>
            {
                AutomationProperties.SetName(_toolsGrid, SettingsText.Text("settings.tools.name", "Tool Name") + " (read-only)");
                AutomationProperties.SetHelpText(_toolsGrid, SettingsText.Text("settings.tools.help", "Built-in and permitted baked tools exposed by the connected server."));
            });
            AddToolsColumn(new DataGridTextColumn
            {
                Binding = new Binding(nameof(ToolRow.No)), SortMemberPath = "No", Width = 40,
                ElementStyle = CellTextStyle(foreground: SettingsStyles.Secondary),
            }, "settings.tools.no", "No.");
            AddToolsColumn(new DataGridTextColumn
            {
                Binding = new Binding(nameof(ToolRow.Name)), SortMemberPath = "Name",
                Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 150,
                ElementStyle = CellTextStyle(toolTip: new Binding(nameof(ToolRow.ToolTipText))),
            }, "settings.tools.name", "Tool Name");
            AddToolsColumn(new DataGridTextColumn
            {
                Binding = new Binding(nameof(ToolRow.DescriptionDisplay)), SortMemberPath = "Description",
                Width = new DataGridLength(1.4, DataGridLengthUnitType.Star),
                ElementStyle = CellTextStyle(toolTip: new Binding(nameof(ToolRow.ToolTipText))),
            }, "settings.tools.description", "Description");
            AddToolsColumn(new DataGridTemplateColumn
            {
                SortMemberPath = "Source", Width = 80, CellTemplate = SourceBadgeTemplate(),
            }, "settings.tools.source", "Source");
            AddToolsColumn(new DataGridTextColumn
            {
                Binding = new Binding(nameof(ToolRow.TimeoutDisplay)), SortMemberPath = "Timeout", Width = 104,
                ElementStyle = CellTextStyle(toolTip: new Binding(nameof(ToolRow.TimeoutHelpText))),
            }, "settings.tools.timeout", "Time-out");
            var rowStyle = new Style(typeof(DataGridRow), (Style)FindResource(typeof(DataGridRow)));
            rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(ToolRow.ToolTipText))));
            _toolsGrid.RowStyle = rowStyle;
            _toolsGrid.Sorting += OnToolsGridSorting;
            _toolsGrid.Columns[1].SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            page.Children.Add(new Border
            {
                Background = SettingsStyles.Surface,
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0, 0, 0, 4),
                Child = _toolsGrid,
            });
            return page;
        }

        private UIElement BuildToolSearch()
        {
            var host = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            _toolSearch = new TextBox
            {
                Height = 30,
                Padding = new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = SettingsStyles.Surface,
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(1),
                Foreground = SettingsStyles.Text,
            };
            _toolSearchPlaceholder = new TextBlock
            {
                Margin = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = SettingsStyles.Secondary,
                IsHitTestVisible = false,
            };
            _localizedText.Add(() =>
            {
                var caption = SettingsText.Text("settings.tools.search", "Search");
                var hint = SettingsText.Text("settings.tools.search.placeholder", "Name or description");
                _toolSearchPlaceholder.Text = hint;
                _toolSearch.ToolTip = hint;
                AutomationProperties.SetName(_toolSearch, caption);
            });
            _toolSearch.TextChanged += (_, __) =>
            {
                _toolSearchPlaceholder.Visibility = string.IsNullOrEmpty(_toolSearch.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            };
            host.Children.Add(_toolSearch);
            host.Children.Add(_toolSearchPlaceholder);
            return host;
        }

        private void AddToolsColumn(DataGridColumn column, string key, string fallback)
        {
            _toolsGrid.Columns.Add(column);
            _localizedText.Add(() => column.Header = SettingsText.Text(key, fallback));
        }

        private static Style CellTextStyle(Brush foreground = null, Binding toolTip = null)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            if (foreground != null) style.Setters.Add(new Setter(TextBlock.ForegroundProperty, foreground));
            if (toolTip != null) style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, toolTip));
            return style;
        }

        private static DataTemplate SourceBadgeTemplate()
        {
            var badge = new FrameworkElementFactory(typeof(Border));
            badge.SetValue(Border.BackgroundProperty, SettingsStyles.NeutralFill);
            badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            badge.SetValue(Border.PaddingProperty, new Thickness(8, 1, 8, 2));
            badge.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            badge.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ToolRow.ToolTipText)));
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ToolRow.Source)));
            text.SetValue(TextBlock.FontSizeProperty, 11.0);
            text.SetValue(TextBlock.ForegroundProperty, SettingsStyles.Secondary);
            badge.AppendChild(text);
            return new DataTemplate { VisualTree = badge };
        }

        private UIElement BuildAbout()
        {
            var page = Page();
            page.Children.Add(new TextBlock { Text = _aboutInfo.ProductName, FontSize = 20, FontWeight = FontWeights.SemiBold });
            var version = RowCaptionText();
            version.FontSize = 13;
            _localizedText.Add(() => version.Text = SettingsText.Text("settings.about.version", "Version") + " " + _aboutInfo.PluginInformationalVersion);
            page.Children.Add(version);
            var description = RowCaption("settings.about.description", "MCP connectivity and activity tools for Autodesk Revit.");
            description.FontSize = 13;
            description.Margin = new Thickness(0, 6, 0, 12);
            page.Children.Add(description);

            var license = new StackPanel { Orientation = Orientation.Horizontal };
            license.Children.Add(ValueText(_aboutInfo.LicenseId));
            var viewLicense = LinkButton("settings.about.viewLicense", "View license", () => new LicenseWindow(_aboutInfo, this).ShowDialog(), external: false);
            viewLicense.Margin = new Thickness(16, 0, 0, 0);
            license.Children.Add(viewLicense);
            page.Children.Add(Card(
                Row(RowLabel("settings.about.author", "Author"), null, ValueText(_aboutInfo.Author)),
                Row(RowLabel("settings.about.license", "License"), null, license),
                Row(RowLabel("settings.about.copyright", "Copyright"), null, ValueText(_aboutInfo.Copyright))));

            var links = new WrapPanel { Margin = new Thickness(0, -6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            links.Children.Add(LinkButton("settings.about.github", "GitHub", () => OpenUrl(_aboutInfo.RepositoryUrl), external: true));
            links.Children.Add(LinkButton("settings.about.issues", "Report a bug", () => OpenUrl(_aboutInfo.IssuesUrl), external: true));
            page.Children.Add(links);
            return page;
        }

        private Button LinkButton(string key, string fallback, Action action, bool external)
        {
            var button = new Button { Style = (Style)FindResource("Link"), Margin = new Thickness(12, 0, 12, 0) };
            _localizedText.Add(() =>
            {
                var text = SettingsText.Text(key, fallback);
                button.Content = external ? text + " ↗" : text;
                AutomationProperties.SetName(button, text);
            });
            button.Click += (_, __) => action();
            return button;
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private UIElement BuildFooter()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _footerDot = new Ellipse { Width = 8, Height = 8, Fill = SettingsStyles.WarningDot, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _footerText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            status.Children.Add(_footerDot);
            status.Children.Add(_footerText);
            grid.Children.Add(status);
            _discard = new Button { Margin = new Thickness(12, 0, 8, 0) };
            LocalizedButton(_discard, "settings.discard", "Discard changes");
            Grid.SetColumn(_discard, 1);
            grid.Children.Add(_discard);
            _apply = new Button { Style = (Style)FindResource("Primary"), MinWidth = 88 };
            LocalizedButton(_apply, "settings.apply", "Apply");
            Grid.SetColumn(_apply, 2);
            grid.Children.Add(_apply);
            return new Border
            {
                Background = SettingsStyles.Surface,
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 10, 20, 10),
                Child = grid,
            };
        }

        // ── Layout primitives ─────────────────────────────────────────────

        private static StackPanel Page() => new StackPanel { Margin = new Thickness(0, 0, 16, 4) };

        private static TextBlock SectionTitleText() =>
            new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

        private static TextBlock RowLabelText() =>
            new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };

        private static TextBlock RowCaptionText() =>
            new TextBlock { FontSize = 12, Foreground = SettingsStyles.Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

        private static TextBlock ValueText(string text = null) =>
            new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };

        private TextBlock SectionTitle(string key, string fallback) => Localized(SectionTitleText(), key, fallback);
        private TextBlock RowLabel(string key, string fallback) => Localized(RowLabelText(), key, fallback);
        private TextBlock RowCaption(string key, string fallback) => Localized(RowCaptionText(), key, fallback);

        private TextBlock Localized(TextBlock text, string key, string fallback)
        {
            _localizedText.Add(() => text.Text = SettingsText.Text(key, fallback));
            return text;
        }

        private void LocalizedButton(Button button, string key, string fallback)
        {
            _localizedText.Add(() =>
            {
                var text = SettingsText.Text(key, fallback);
                button.Content = text;
                AutomationProperties.SetName(button, text);
            });
        }

        /// <summary>Controls without their own caption take the row label as their UI Automation name.</summary>
        private void NameFromLabel(FrameworkElement control, TextBlock label)
        {
            _localizedText.Add(() => AutomationProperties.SetName(control, label.Text));
        }

        private CheckBox Switch(TextBlock label)
        {
            var toggle = new CheckBox();
            NameFromLabel(toggle, label);
            return toggle;
        }

        /// <summary>One setting: label and caption on the left, control on the right, inline error below.</summary>
        private Grid Row(TextBlock label, TextBlock caption, FrameworkElement control, params string[] errorKeys)
        {
            var row = new Grid { Margin = new Thickness(14, 7, 14, 7), MinHeight = 28 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(label);
            if (caption != null) text.Children.Add(caption);
            foreach (var errorKey in errorKeys)
            {
                var error = new TextBlock { FontSize = 12, Foreground = SettingsStyles.Error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
                _fieldErrors[errorKey] = error;
                text.Children.Add(error);
            }
            row.Children.Add(text);
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private static Border Card(params UIElement[] entries)
        {
            var stack = new StackPanel();
            for (var i = 0; i < entries.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 1, Background = SettingsStyles.Line, Margin = new Thickness(14, 0, 14, 0) });
                stack.Children.Add(entries[i]);
            }
            return new Border
            {
                Background = SettingsStyles.Surface,
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 16),
                Child = stack,
            };
        }

        // ── State → view ──────────────────────────────────────────────────

        private sealed class JournalHoursConverter : IValueConverter
        {
            internal static string Label(int hours) => hours >= 48 && hours % 24 == 0 ? (hours / 24) + "d" : hours + "h";
            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is int hours ? Label(hours) : "";
            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
        }

        private void OnCopyPort(object sender, RoutedEventArgs e)
        {
            _copyPort.Content = SettingsText.Text("settings.footer.copied", "Copied");
            _copiedTimer?.Stop();
            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _copiedTimer.Tick += (_, __) =>
            {
                _copiedTimer.Stop();
                _copyPort.Content = SettingsText.Text("settings.general.copy", "Copy");
            };
            _copiedTimer.Start();
        }

        private void UpdateDynamicText()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateDynamicText));
                return;
            }
            UpdateConnection();
            _languageCaption.Text = InvariantLanguageText.CurrentlyUsingPrefix + LocaleResolver.NativeName(_viewModel.EffectiveLanguage)
                + " · " + InvariantLanguageText.SourcePrefix + (_viewModel.Snapshot?.LocaleSource ?? InvariantLanguageText.AutoOption);
            _language.ToolTip = _viewModel.IsLanguageWriteDisabled ? InvariantLanguageText.OverrideToolTip : InvariantLanguageText.SelectionToolTip;
            _warningPolicyCaption.Text = SettingsText.Text("security.warningPolicy.help",
                "Rollback is the default. Accept and log applies only to supported warnings with complete capture and durable recording.")
                + (_viewModel.IsWarningPolicyWriteDisabled ? " " + SettingsText.Text("security.warningPolicy.environmentOverride",
                    "BIMWRIGHT_WARNING_POLICY overrides the saved setting in this Revit process.") : "");
            AutomationProperties.SetHelpText(_warningPolicy, _warningPolicyCaption.Text);
            // Legacy values remain display-only; they never add a selectable preset or change the model.
            _journalHours.IsEditable = !_journalHours.Items.Contains(_viewModel.PersistSendCodeBodiesHours);
            if (_journalHours.IsEditable) _journalHours.Text = JournalHoursConverter.Label(_viewModel.PersistSendCodeBodiesHours);
            UpdateErrors();
            UpdateFooter();
            UpdateToolsText();
        }

        private void UpdateConnection()
        {
            if (_viewModel.IsClientConnected)
                SetStatePill(SettingsStyles.SuccessFill, SettingsStyles.SuccessDot, SettingsStyles.SuccessText);
            else if (_viewModel.IsListenerRunning)
                SetStatePill(SettingsStyles.WarningFill, SettingsStyles.WarningDot, SettingsStyles.WarningText);
            else
                SetStatePill(SettingsStyles.NeutralFill, SettingsStyles.NeutralDot, SettingsStyles.Secondary);
            _stateText.Text = _viewModel.ConnectionState;
            _transportText.Inlines.Clear();
            _transportText.Inlines.Add(new Run(SettingsText.Text("settings.general.transport", "Transport type") + ": "));
            _transportText.Inlines.Add(new Run(_viewModel.TransportKind) { FontWeight = FontWeights.SemiBold });
            _transportText.Inlines.Add(new Run("   |   ") { Foreground = SettingsStyles.NeutralDot });
            _transportText.Inlines.Add(new Run(SettingsText.Text("settings.general.port", "Port") + ": "));
            _transportText.Inlines.Add(new Run(_viewModel.PortText) { FontWeight = FontWeights.SemiBold });
            _copyPort.ToolTip = _viewModel.CanCopyPort ? null : SettingsText.Text("settings.footer.portUnavailable", "Port is not available");
        }

        private void SetStatePill(Brush fill, Brush dot, Brush text)
        {
            _statePill.Background = fill;
            _stateDot.Fill = dot;
            _stateText.Foreground = text;
        }

        /// <summary>Apply failures and the latest immediate-save warning render under their row;
        /// anything without a row falls back to the footer.</summary>
        private void UpdateErrors()
        {
            var messages = _viewModel.LastApplyReport == null ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(_viewModel.LastApplyReport.FieldErrors, StringComparer.Ordinal);
            var warning = _viewModel.ImmediateWarning;
            var warningKey = _viewModel.ImmediateWarningKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(warning) && !messages.ContainsKey(warningKey))
                messages[warningKey] = warning;
            foreach (var field in _fieldErrors)
            {
                string message;
                messages.TryGetValue(field.Key, out message);
                field.Value.Text = message ?? string.Empty;
                field.Value.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            }
            _footerError = string.Join("; ", messages.Where(pair => !_fieldErrors.ContainsKey(pair.Key)).Select(pair => pair.Value));
        }

        private void UpdateFooter()
        {
            var dirty = _viewModel.IsDirty;
            _footerDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(_footerError))
            {
                _footerText.Text = _footerError;
                _footerText.Foreground = SettingsStyles.Error;
            }
            else
            {
                _footerText.Text = dirty ? SettingsText.Text("settings.footer.unsaved", "Unsaved changes") : (_viewModel.Feedback ?? string.Empty);
                _footerText.Foreground = SettingsStyles.Secondary;
            }
        }

        private void UpdateLocalizedChrome()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateLocalizedChrome));
                return;
            }
            Title = SettingsText.Text("settings.window.title", "rvt-mcp · Settings");
            _tabItems[SettingsTab.General].Header = SettingsText.Text("settings.tab.general", "General");
            _tabItems[SettingsTab.Toast].Header = SettingsText.Text("settings.tab.toast", "Toast");
            // Tools is an invariant tab caption by contract.
            _tabItems[SettingsTab.Tools].Header = "Tools";
            _tabItems[SettingsTab.About].Header = SettingsText.Text("settings.tab.about", "About");
            foreach (var update in _localizedText) update();
        }

        private void UpdateToolsText()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateToolsText));
                return;
            }
            if (_toolsStatus == null) return;
            _toolsCounts.Text = _toolsViewModel.CountsText;
            _toolsStatus.Text = _toolsViewModel.StatusText;
            _toolsStatus.Visibility = string.IsNullOrWhiteSpace(_toolsViewModel.StatusText) ? Visibility.Collapsed : Visibility.Visible;
            _toolsStatus.ToolTip = _toolsViewModel.StatusHelpText;
            AutomationProperties.SetName(_toolsStatus, SettingsText.Text("settings.tools.status.label", "Catalog status"));
            AutomationProperties.SetHelpText(_toolsStatus,
                !string.IsNullOrWhiteSpace(_toolsViewModel.StatusHelpText) ? _toolsViewModel.StatusHelpText
                : !string.IsNullOrWhiteSpace(_toolsViewModel.StatusText) ? _toolsViewModel.StatusText
                : SettingsText.Text("settings.tools.help", "Built-in and permitted baked tools exposed by the connected server."));
        }

        // ── Events ────────────────────────────────────────────────────────

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateDynamicText();
        }

        private void OnViewModelLanguageChanged(object sender, EventArgs e)
        {
            UpdateLocalizedChrome();
            UpdateDynamicText();
        }

        private void OnToolsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateToolsText();
        }

        private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Child selectors (grid, combos) bubble SelectionChanged here; only a real tab switch counts.
            if (e.OriginalSource != _tabs) return;
            if (_tabs.SelectedItem == _tabItems[SettingsTab.Tools])
                _toolsViewModel.Refresh();
        }

        private void OnToolsGridSorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            _toolsViewModel.SortBy(e.Column.SortMemberPath);
            var direction = _toolsViewModel.SortAscending
                ? System.ComponentModel.ListSortDirection.Ascending
                : System.ComponentModel.ListSortDirection.Descending;
            foreach (var column in _toolsGrid.Columns)
                column.SortDirection = string.Equals(column.SortMemberPath, _toolsViewModel.SortColumn, StringComparison.Ordinal)
                    ? direction
                    : (System.ComponentModel.ListSortDirection?)null;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closing || !_viewModel.IsDirty) return;
            var answer = MessageBox.Show(this,
                SettingsText.Text("settings.footer.discardPrompt", "You have unapplied changes. Close without applying them?"),
                SettingsText.Text("settings.window.title", "rvt-mcp · Settings"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
            else _closing = true;
        }

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _copiedTimer?.Stop();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.LanguageChanged -= OnViewModelLanguageChanged;
            _toolsViewModel.PropertyChanged -= OnToolsPropertyChanged;
            _tabs.SelectionChanged -= OnTabsSelectionChanged;
            PreviewKeyDown -= OnPreviewKeyDown;
            foreach (var control in _boundControls) BindingOperations.ClearAllBindings(control);
            _boundControls.Clear();
            DataContext = null;
            _viewModel.Dispose();
            _toolsViewModel.Dispose();
        }
    }
}
