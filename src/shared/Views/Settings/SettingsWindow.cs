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
        private readonly TabControl _tabs;
        private readonly Dictionary<SettingsTab, TabItem> _tabItems = new Dictionary<SettingsTab, TabItem>();
        // Re-run on every language change so all localized chrome follows L.Changed.
        private readonly List<Action> _localizedText = new List<Action>();
        // Inline error line under each setting row, keyed by config key.
        private readonly Dictionary<string, TextBlock> _fieldErrors = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _applyErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _closing;
        private bool _syncingControls;
        private string _feedback;
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
        private CheckBox _cacheSendCodeBodies;
        private CheckBox _persistSendCodeBodies;
        private ComboBox _journalHours;
        private CheckBox _toastEnabled;
        private CheckBox _showBranding;
        private ComboBox _toastIdle;
        private DataGrid _toolsGrid;
        private TextBlock _toolsCounts;
        private TextBlock _toolsStatus;
        private string _toolsSelectedName;
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

            Loaded += (_, __) => SelectTab(initialTab);
            Closing += OnClosing;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.LanguageChanged += OnViewModelLanguageChanged;
            _toolsViewModel.PropertyChanged += OnToolsPropertyChanged;
            _toolsViewModel.Rows.CollectionChanged += OnToolsRowsChanged;
            _tabs.SelectionChanged += OnTabsSelectionChanged;
            ToolCatalogStore.Changed += OnCatalogStoreChanged;
            SelectTab(initialTab);
            UpdateLocalizedChrome();
            UpdateDynamicText();
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
            _restartListener.Click += (_, __) => _viewModel.RestartListener();
            _listenerEnabled = new CheckBox { Margin = new Thickness(4, 0, 0, 0) };
            _localizedText.Add(() =>
            {
                var text = SettingsText.Text("settings.general.listener", "MCP listener");
                _listenerEnabled.ToolTip = text;
                AutomationProperties.SetName(_listenerEnabled, text);
            });
            _listenerEnabled.Checked += (_, __) => { if (!_syncingControls) _viewModel.SetListenerRunning(true); };
            _listenerEnabled.Unchecked += (_, __) => { if (!_syncingControls) _viewModel.SetListenerRunning(false); };
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
            _language.SelectionChanged += OnLanguageSelectionChanged;
            AutomationProperties.SetName(_language, InvariantLanguageText.AutomationName);
            AutomationProperties.SetHelpText(_language, InvariantLanguageText.AutomationHelp);
            var languageLabel = RowLabelText();
            languageLabel.Text = InvariantLanguageText.RowLabel;
            _languageCaption = RowCaptionText();
            page.Children.Add(Card(Row(languageLabel, _languageCaption, _language, "uiLanguage")));

            page.Children.Add(SectionTitle("settings.general.privacy", "Privacy"));
            var cacheLabel = RowLabel("settings.general.cacheBodies", "Cache send_code bodies");
            _cacheSendCodeBodies = Switch(cacheLabel);
            _cacheSendCodeBodies.Checked += (_, __) => { if (!_syncingControls) _viewModel.CacheSendCodeBodies = true; };
            _cacheSendCodeBodies.Unchecked += (_, __) => { if (!_syncingControls) _viewModel.CacheSendCodeBodies = false; };
            var journalLabel = RowLabel("settings.general.keepJournal", "Keep send_code journal");
            _persistSendCodeBodies = Switch(journalLabel);
            _persistSendCodeBodies.Checked += (_, __) => { if (!_syncingControls) _viewModel.PersistSendCodeBodies = true; };
            _persistSendCodeBodies.Unchecked += (_, __) => { if (!_syncingControls) _viewModel.PersistSendCodeBodies = false; };
            // The duration sits beside the journal switch it belongs to; its label names/tooltips it.
            _journalHours = new ComboBox { Width = 84, ItemsSource = Enumerable.Range(1, 48).ToArray(), ItemStringFormat = "{0} h", Margin = new Thickness(0, 0, 12, 0) };
            _localizedText.Add(() =>
            {
                var hoursText = SettingsText.Text("settings.general.journalDuration", "Journal duration (hours)");
                _journalHours.ToolTip = hoursText;
                AutomationProperties.SetName(_journalHours, hoursText);
            });
            _journalHours.SelectionChanged += (_, __) =>
            {
                if (!_syncingControls && _journalHours.SelectedItem is int value) _viewModel.PersistSendCodeBodiesHours = value;
            };
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
            _toastEnabled.Checked += (_, __) => { if (!_syncingControls) _viewModel.SetToastEnabled(true); };
            _toastEnabled.Unchecked += (_, __) => { if (!_syncingControls) _viewModel.SetToastEnabled(false); };
            var brandLabel = RowLabel("settings.toast.brand", "Show branding");
            _showBranding = Switch(brandLabel);
            _showBranding.Checked += (_, __) => { if (!_syncingControls) _viewModel.SetShowBranding(true); };
            _showBranding.Unchecked += (_, __) => { if (!_syncingControls) _viewModel.SetShowBranding(false); };
            var idleLabel = RowLabel("settings.toast.idle", "Idle duration");
            _toastIdle = new ComboBox { Width = 120, ItemsSource = ToastIdleOptions, ItemStringFormat = "{0} s" };
            NameFromLabel(_toastIdle, idleLabel);
            _toastIdle.SelectionChanged += (_, __) =>
            {
                if (!_syncingControls && _toastIdle.SelectedItem is int value) _viewModel.ToastIdleSeconds = value;
            };
            page.Children.Add(Card(
                Row(enabledLabel, RowCaption("settings.toast.enabled.help", "Takes effect immediately."), _toastEnabled, "enableToast"),
                Row(brandLabel, RowCaption("settings.toast.brand.help", "Appears when you point at the activity card. Applies immediately and lasts until Revit restarts."), _showBranding),
                Row(idleLabel,
                    RowCaption("settings.toast.idle.help", "Hides the card when no new results arrive. Hover to keep it open. Applies from the next activity."),
                    _toastIdle, "toastIdleSeconds")));
            return page;
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
            refresh.Click += (_, __) => _toolsViewModel.Refresh();
            Grid.SetColumn(refresh, 1);
            toolbar.Children.Add(refresh);
            DockPanel.SetDock(toolbar, Dock.Top);
            page.Children.Add(toolbar);

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
                ItemsSource = _toolsViewModel.Rows,
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
                ElementStyle = CellTextStyle(toolTip: new Binding(nameof(ToolRow.Name))),
            }, "settings.tools.name", "Tool Name");
            AddToolsColumn(new DataGridTextColumn
            {
                Binding = new Binding(nameof(ToolRow.DescriptionDisplay)), SortMemberPath = "Description",
                Width = new DataGridLength(1.4, DataGridLengthUnitType.Star),
                ElementStyle = CellTextStyle(toolTip: new Binding(nameof(ToolRow.DescriptionDisplay))),
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
            _toolsGrid.Sorting += OnToolsGridSorting;
            _toolsGrid.SelectionChanged += OnToolsGridSelectionChanged;
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
            _discard.Click += (_, __) =>
            {
                _viewModel.Cancel();
                _applyErrors.Clear();
                _feedback = null;
                UpdateDynamicText();
            };
            Grid.SetColumn(_discard, 1);
            grid.Children.Add(_discard);
            _apply = new Button { Style = (Style)FindResource("Primary"), MinWidth = 88 };
            LocalizedButton(_apply, "settings.apply", "Apply");
            _apply.Click += OnApply;
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

        private void SetSelectedLanguage()
        {
            var selected = _language.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, _viewModel.SelectedLanguage, StringComparison.Ordinal));
            _language.SelectedItem = selected ?? _language.Items[0];
        }

        private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingControls) return;
            var selected = _language.SelectedItem as ComboBoxItem;
            if (selected != null) _viewModel.SetLanguage(selected.Tag as string);
            UpdateDynamicText();
        }

        private void OnCopyPort(object sender, RoutedEventArgs e)
        {
            _viewModel.CopyPort();
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

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var report = _viewModel.Apply();
            _applyErrors.Clear();
            foreach (var error in report.FieldErrors) _applyErrors[error.Key] = error.Value;
            _feedback = report.Succeeded ? SettingsText.Text("settings.footer.applied", "Applied") : null;
            UpdateDynamicText();
        }

        private void UpdateDynamicText()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateDynamicText));
                return;
            }
            UpdateConnection();
            _syncingControls = true;
            try
            {
                SetSelectedLanguage();
                _language.IsEnabled = !_viewModel.IsLanguageWriteDisabled;
                _language.ToolTip = _viewModel.IsLanguageWriteDisabled
                    ? InvariantLanguageText.OverrideToolTip
                    : InvariantLanguageText.SelectionToolTip;
                _languageCaption.Text = InvariantLanguageText.CurrentlyUsingPrefix + LocaleResolver.NativeName(_viewModel.EffectiveLanguage)
                    + " · " + InvariantLanguageText.SourcePrefix + (_viewModel.Snapshot?.LocaleSource ?? InvariantLanguageText.AutoOption);
                _listenerEnabled.IsChecked = _viewModel.IsListenerRunning;
                _toastEnabled.IsChecked = _viewModel.ToastEnabled;
                _showBranding.IsChecked = _viewModel.ShowBranding;
                _showBranding.IsEnabled = _viewModel.ToastEnabled;
                _toastIdle.SelectedItem = _viewModel.ToastIdleSeconds;
                _toastIdle.IsEnabled = _viewModel.ToastEnabled;
                _cacheSendCodeBodies.IsChecked = _viewModel.CacheSendCodeBodies;
                _persistSendCodeBodies.IsChecked = _viewModel.PersistSendCodeBodies;
                _journalHours.SelectedItem = _viewModel.PersistSendCodeBodiesHours;
            }
            finally
            {
                _syncingControls = false;
            }
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
            _restartListener.IsEnabled = _viewModel.IsListenerRunning;
            _transportText.Inlines.Clear();
            _transportText.Inlines.Add(new Run(SettingsText.Text("settings.general.transport", "Transport type") + ": "));
            _transportText.Inlines.Add(new Run(_viewModel.TransportKind) { FontWeight = FontWeights.SemiBold });
            _transportText.Inlines.Add(new Run("   |   ") { Foreground = SettingsStyles.NeutralDot });
            _transportText.Inlines.Add(new Run(SettingsText.Text("settings.general.port", "Port") + ": "));
            _transportText.Inlines.Add(new Run(_viewModel.PortText) { FontWeight = FontWeights.SemiBold });
            _copyPort.IsEnabled = _viewModel.CanCopyPort;
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
            var messages = new Dictionary<string, string>(_applyErrors, StringComparer.Ordinal);
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
            if (dirty) _feedback = null;
            _apply.IsEnabled = dirty;
            _discard.IsEnabled = dirty;
            _footerDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(_footerError))
            {
                _footerText.Text = _footerError;
                _footerText.Foreground = SettingsStyles.Error;
            }
            else
            {
                _footerText.Text = dirty ? SettingsText.Text("settings.footer.unsaved", "Unsaved changes") : (_feedback ?? string.Empty);
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

        private void OnCatalogStoreChanged(object sender, EventArgs e)
        {
            // Fires on the transport thread (BeginConnection/Clear); UpdateDynamicText marshals.
            UpdateDynamicText();
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

        private void OnToolsGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = _toolsGrid.SelectedItem as ToolRow;
            if (row != null) _toolsSelectedName = row.Name;
        }

        private void OnToolsRowsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_toolsSelectedName == null || _toolsGrid == null) return;
            var match = _toolsViewModel.Rows.FirstOrDefault(row => string.Equals(row.Name, _toolsSelectedName, StringComparison.Ordinal));
            if (match != null && !ReferenceEquals(_toolsGrid.SelectedItem, match))
                _toolsGrid.SelectedItem = match;
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
            _toolsViewModel.Rows.CollectionChanged -= OnToolsRowsChanged;
            _tabs.SelectionChanged -= OnTabsSelectionChanged;
            ToolCatalogStore.Changed -= OnCatalogStoreChanged;
            PreviewKeyDown -= OnPreviewKeyDown;
            _viewModel.Dispose();
            _toolsViewModel.Dispose();
        }
    }
}
