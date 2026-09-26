using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>
    /// Modeless, owner-bound Settings shell. Controls are built in code so all six
    /// plugin shells share the same layout without loose XAML files.
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private readonly SettingsViewModel _viewModel;
        private readonly ToolsViewModel _toolsViewModel;
        private readonly TabControl _tabs;
        private readonly Dictionary<SettingsTab, TabItem> _tabItems = new Dictionary<SettingsTab, TabItem>();
        private readonly List<Action> _dynamicUpdates = new List<Action>();
        private bool _closing;
        private bool _updatingLanguage;
        private bool _updatingToast;

        private TextBlock _status;
        private TextBlock _languageEffective;
        private TextBlock _languageSource;
        private TextBlock _revitValue;
        private TextBlock _transportValue;
        private TextBlock _portValue;
        private TextBlock _connectionStateValue;
        private ComboBox _language;
        private ComboBox _toastIdle;
        private ComboBox _journalHours;
        private CheckBox _toastEnabled;
        private CheckBox _cacheSendCodeBodies;
        private CheckBox _persistSendCodeBodies;
        private Button _apply;
        private Button _cancel;
        private Button _close;
        private TextBlock _connectionHeading;
        private TextBlock _portLabel;
        private TextBlock _privacyHeading;
        private TextBlock _journalLabel;
        private TextBlock _toastHeading;
        private TextBlock _toastIdleLabel;
        private TextBlock _toastHelp;
        private TextBlock _aboutDescription;
        private TextBlock _aboutHint;
        private AboutInfo _aboutInfo;
        private Button _aboutLicense;
        private Button _aboutRepository;
        private Button _aboutDocumentation;
        private Button _aboutIssues;
        private Button _copyPort;
        private DataGrid _toolsGrid;
        private TextBlock _toolsStatus;
        private TextBlock _toolsCounts;
        private Button _toolsRefresh;

        public SettingsWindow(App app, SettingsTab initialTab = SettingsTab.General)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            _viewModel = new SettingsViewModel(app);
            _toolsViewModel = new ToolsViewModel(app.BakedToolRegistry, Assembly.GetExecutingAssembly());
            DataContext = _viewModel;
            Title = "rvt-mcp · Settings";
            Width = 760;
            Height = 560;
            MinWidth = 640;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            Resources.MergedDictionaries.Add(BimwrightStyles.Dictionary);

            var root = new DockPanel { Margin = new Thickness(18) };
            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            _tabs = new TabControl { TabStripPlacement = Dock.Top };
            AddTab(SettingsTab.General, "General", BuildGeneral());
            AddTab(SettingsTab.Toast, "Toast", BuildToast());
            AddTab(SettingsTab.Tools, "Tools", BuildTools());
            AddTab(SettingsTab.About, "About", BuildAbout());
            root.Children.Add(_tabs);
            Content = root;

            Loaded += (_, __) => SelectTab(initialTab);
            Closing += OnClosing;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.LanguageChanged += OnViewModelLanguageChanged;
            _toolsViewModel.PropertyChanged += OnToolsPropertyChanged;
            SelectTab(initialTab);
            UpdateDynamicText();
            UpdateLocalizedChrome();
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

        private void AddTab(SettingsTab key, string title, UIElement content)
        {
            var item = new TabItem
            {
                Header = title,
                Content = new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            };
            AutomationProperties.SetName(item, title + " settings tab");
            _tabItems[key] = item;
            _tabs.Items.Add(item);
        }

        private UIElement BuildGeneral()
        {
            var panel = new StackPanel { Margin = new Thickness(14) };
            _connectionHeading = Heading("Connection");
            panel.Children.Add(_connectionHeading);
            panel.Children.Add(ReadOnlyRow("settings.general.revit", "Revit", () => _viewModel.RevitYear, value => _revitValue = value));
            panel.Children.Add(ReadOnlyRow("settings.general.transport", "Transport", () => _viewModel.TransportKind, value => _transportValue = value));

            var port = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            _portLabel = new TextBlock { Text = "Port", Width = 170, VerticalAlignment = VerticalAlignment.Center };
            port.Children.Add(_portLabel);
            _portValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            port.Children.Add(_portValue);
            _copyPort = new Button { Content = "Copy", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), TabIndex = 0 };
            AutomationProperties.SetName(_copyPort, "Copy TCP port");
            AutomationProperties.SetHelpText(_copyPort, "Copies the active TCP port when a TCP listener is running.");
            _copyPort.Click += (_, __) =>
            {
                _viewModel.CopyPort();
                _status.Text = _viewModel.CanCopyPort ? "Copied" : "Port is not available";
            };
            DockPanel.SetDock(_copyPort, Dock.Right);
            port.Children.Add(_copyPort);
            panel.Children.Add(port);
            panel.Children.Add(ReadOnlyRow("settings.general.state", "State", () => _viewModel.ConnectionState, value => _connectionStateValue = value));

            // Language is intentionally invariant English per the Settings contract.
            panel.Children.Add(Heading("LANGUAGE"));
            _language = new ComboBox { Width = 260, TabIndex = 1 };
            _language.Items.Add(new ComboBoxItem { Content = "Auto — follow Revit", Tag = LocaleResolver.Auto });
            foreach (var locale in LocaleResolver.SupportedLocales)
                _language.Items.Add(new ComboBoxItem { Content = LocaleResolver.NativeName(locale), Tag = locale });
            _language.SelectionChanged += OnLanguageSelectionChanged;
            AutomationProperties.SetName(_language, "Language");
            AutomationProperties.SetHelpText(_language, "Select a session language. An environment override disables this control.");
            SetSelectedLanguage();
            panel.Children.Add(_language);
            _languageEffective = new TextBlock { Margin = new Thickness(0, 5, 0, 0) };
            _languageSource = new TextBlock { Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 12) };
            panel.Children.Add(_languageEffective);
            panel.Children.Add(_languageSource);

            _privacyHeading = Heading("Privacy");
            panel.Children.Add(_privacyHeading);
            _cacheSendCodeBodies = new CheckBox { Content = "Cache send_code bodies", IsChecked = _viewModel.CacheSendCodeBodies, TabIndex = 2 };
            _cacheSendCodeBodies.Checked += (_, __) => _viewModel.CacheSendCodeBodies = true;
            _cacheSendCodeBodies.Unchecked += (_, __) => _viewModel.CacheSendCodeBodies = false;
            panel.Children.Add(_cacheSendCodeBodies);

            _persistSendCodeBodies = new CheckBox { Content = "Keep send_code journal", IsChecked = _viewModel.PersistSendCodeBodies, TabIndex = 3 };
            _persistSendCodeBodies.Checked += (_, __) => _viewModel.PersistSendCodeBodies = true;
            _persistSendCodeBodies.Unchecked += (_, __) => _viewModel.PersistSendCodeBodies = false;
            panel.Children.Add(_persistSendCodeBodies);
            var hoursRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            _journalLabel = new TextBlock { Text = "Journal duration (hours)", Width = 170, VerticalAlignment = VerticalAlignment.Center };
            hoursRow.Children.Add(_journalLabel);
            _journalHours = new ComboBox { Width = 120, ItemsSource = Enumerable.Range(1, 48).ToArray(), SelectedItem = _viewModel.PersistSendCodeBodiesHours, TabIndex = 4 };
            _journalHours.SelectionChanged += (_, __) => { if (_journalHours.SelectedItem is int value) _viewModel.PersistSendCodeBodiesHours = value; };
            hoursRow.Children.Add(_journalHours);
            panel.Children.Add(hoursRow);
            return panel;
        }

        private UIElement BuildToast()
        {
            var panel = new StackPanel { Margin = new Thickness(14) };
            _toastHeading = Heading("Toast");
            panel.Children.Add(_toastHeading);
            _toastEnabled = new CheckBox { Content = "Show activity notifications", IsChecked = _viewModel.ToastEnabled, TabIndex = 5 };
            AutomationProperties.SetName(_toastEnabled, "Show activity notifications");
            _toastEnabled.Checked += (_, __) => { if (!_updatingToast) _viewModel.SetToastEnabled(true); };
            _toastEnabled.Unchecked += (_, __) => { if (!_updatingToast) _viewModel.SetToastEnabled(false); };
            panel.Children.Add(_toastEnabled);
            _toastIdleLabel = new TextBlock { Text = "Idle duration (applies on the next activity)", Margin = new Thickness(0, 14, 0, 4) };
            panel.Children.Add(_toastIdleLabel);
            _toastIdle = new ComboBox { ItemsSource = new[] { 10, 20, 30, 60 }, SelectedItem = _viewModel.ToastIdleSeconds, Width = 120, TabIndex = 6 };
            _toastIdle.SelectionChanged += (_, __) => { if (_toastIdle.SelectedItem is int value) _viewModel.ToastIdleSeconds = value; };
            panel.Children.Add(_toastIdle);
            _toastHelp = new TextBlock { Text = "Toast On/Off is immediate. Apply saves the duration and privacy settings.", Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_toastHelp);
            return panel;
        }

        private UIElement BuildTools()
        {
            var panel = new DockPanel { Margin = new Thickness(14) };
            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var statusStack = new StackPanel { Orientation = Orientation.Vertical };
            _toolsCounts = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _toolsStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            statusStack.Children.Add(_toolsCounts);
            statusStack.Children.Add(_toolsStatus);
            toolbar.Children.Add(statusStack);
            _toolsRefresh = new Button { Content = "Refresh", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), TabIndex = 7 };
            AutomationProperties.SetName(_toolsRefresh, "Refresh tool catalog");
            _toolsRefresh.Click += (_, __) => _toolsViewModel.Refresh();
            DockPanel.SetDock(_toolsRefresh, Dock.Right);
            toolbar.Children.Add(_toolsRefresh);
            DockPanel.SetDock(toolbar, Dock.Top);
            panel.Children.Add(toolbar);

            _toolsGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserResizeRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                ItemsSource = _toolsViewModel.Rows,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                MinColumnWidth = 50,
            };
            AutomationProperties.SetName(_toolsGrid, "Read-only MCP tool catalog");
            AutomationProperties.SetHelpText(_toolsGrid, "Built-in and permitted baked tools exposed by the connected server.");
            _toolsGrid.Columns.Add(new DataGridTextColumn { Header = "No.", Binding = new Binding(nameof(ToolRow.No)), Width = 52 });
            _toolsGrid.Columns.Add(new DataGridTextColumn { Header = "Tool Name", Binding = new Binding(nameof(ToolRow.Name)), Width = 220 });
            _toolsGrid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new Binding(nameof(ToolRow.DescriptionDisplay)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _toolsGrid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new Binding(nameof(ToolRow.Source)), Width = 90 });
            _toolsGrid.Columns.Add(new DataGridTextColumn { Header = "Time-out", Binding = new Binding(nameof(ToolRow.TimeoutDisplay)), Width = 120 });
            panel.Children.Add(_toolsGrid);
            UpdateToolsText();
            return panel;
        }

        private UIElement BuildAbout()
        {
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Heading("rvt-mcp"));
            _aboutDescription = new TextBlock { Text = "MCP connectivity and activity tools for Autodesk Revit.", TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_aboutDescription);
            _aboutHint = new TextBlock { Text = "Version and license information are available in this tab.", Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_aboutHint);
            _aboutInfo = AboutInfoProvider.Create(Assembly.GetExecutingAssembly(), _viewModel.RevitYear);
            panel.Children.Add(ReadOnlyRow("settings.about.product", "Product", () => _aboutInfo.ProductName, _ => { }));
            panel.Children.Add(ReadOnlyRow("settings.about.version", "Version", () => _aboutInfo.PluginInformationalVersion, _ => { }));
            panel.Children.Add(ReadOnlyRow("settings.about.revit", "Revit", () => _viewModel.RevitYear, _ => { }));
            panel.Children.Add(ReadOnlyRow("settings.about.author", "Author", () => _aboutInfo.Author, _ => { }));
            panel.Children.Add(ReadOnlyRow("settings.about.license", "License", () => _aboutInfo.LicenseId, _ => { }));
            panel.Children.Add(ReadOnlyRow("settings.about.copyright", "Copyright", () => _aboutInfo.Copyright, _ => { }));
            var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            _aboutLicense = AboutLinkButton("settings.about.viewLicense", "View license", () => new LicenseWindow(_aboutInfo, this).ShowDialog());
            _aboutRepository = AboutLinkButton("settings.about.github", "GitHub", () => OpenUrl(_aboutInfo.RepositoryUrl));
            _aboutDocumentation = AboutLinkButton("settings.about.docs", "Documentation", () => OpenUrl(_aboutInfo.DocumentationUrl));
            _aboutIssues = AboutLinkButton("settings.about.issues", "Report a bug", () => OpenUrl(_aboutInfo.IssuesUrl));
            links.Children.Add(_aboutLicense); links.Children.Add(_aboutRepository); links.Children.Add(_aboutDocumentation); links.Children.Add(_aboutIssues);
            panel.Children.Add(links);
            return panel;
        }

        private Button AboutLinkButton(string key, string fallback, Action action)
        {
            var button = new Button { Content = Text(key, fallback), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 3, 10, 3) };
            button.Click += (_, __) => action();
            return button;
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private StackPanel BuildFooter()
        {
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _status = new TextBlock { Width = 330, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            footer.Children.Add(_status);
            _apply = new Button { Content = "Apply", Style = (Style)Resources[BimwrightStyles.PrimaryButtonKey], TabIndex = 10 };
            _apply.Click += (_, __) =>
            {
                var result = _viewModel.Apply();
                _status.Text = result.Succeeded ? "Applied" : string.Join("; ", result.Failed);
                UpdateDynamicText();
            };
            _cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0), TabIndex = 11 };
            _cancel.Click += (_, __) => { _viewModel.Cancel(); UpdateDynamicText(); };
            _close = new Button { Content = "Close", Margin = new Thickness(8, 0, 0, 0), TabIndex = 12 };
            _close.Click += (_, __) => Close();
            footer.Children.Add(_apply);
            footer.Children.Add(_cancel);
            footer.Children.Add(_close);
            return footer;
        }

        private TextBlock Heading(string text)
        {
            return new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) };
        }

        private TextBlock ReadOnlyRow(string key, string fallback, Func<string> value, Action<TextBlock> capture)
        {
            var text = new TextBlock { Margin = new Thickness(0, 4, 0, 4) };
            capture(text);
            _dynamicUpdates.Add(() => text.Text = Text(key, fallback) + ": " + value());
            return text;
        }

        private void SetSelectedLanguage()
        {
            if (_language == null) return;
            var selected = _language.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, _viewModel.SelectedLanguage, StringComparison.Ordinal));
            _updatingLanguage = true;
            _language.SelectedItem = selected ?? _language.Items[0];
            _updatingLanguage = false;
        }

        private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingLanguage) return;
            var selected = _language.SelectedItem as ComboBoxItem;
            if (selected != null) _viewModel.SetLanguage(selected.Tag as string);
            UpdateDynamicText();
        }

        private void UpdateDynamicText()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateDynamicText));
                return;
            }
            foreach (var update in _dynamicUpdates) update();
            if (_portValue != null) _portValue.Text = _viewModel.PortText;
            if (_languageEffective != null)
                _languageEffective.Text = "Currently using: " + LocaleResolver.NativeName(_viewModel.EffectiveLanguage);
            if (_languageSource != null)
                _languageSource.Text = "Source: " + (_viewModel.Snapshot?.LocaleSource ?? "Auto — follow Revit");
            if (_language != null)
            {
                _language.IsEnabled = !_viewModel.IsLanguageWriteDisabled;
                _language.ToolTip = _viewModel.IsLanguageWriteDisabled
                    ? "Environment override is active; restart uses BIMWRIGHT_UI_LANGUAGE."
                    : "Select a language; changes apply immediately.";
            }
            if (_toastEnabled != null && _toastEnabled.IsChecked != _viewModel.ToastEnabled)
            {
                _updatingToast = true;
                _toastEnabled.IsChecked = _viewModel.ToastEnabled;
                _updatingToast = false;
            }
            if (_toastIdle != null) _toastIdle.IsEnabled = _viewModel.ToastEnabled;
            if (_cacheSendCodeBodies != null) _cacheSendCodeBodies.IsChecked = _viewModel.CacheSendCodeBodies;
            if (_persistSendCodeBodies != null) _persistSendCodeBodies.IsChecked = _viewModel.PersistSendCodeBodies;
            if (_journalHours != null) _journalHours.SelectedItem = _viewModel.PersistSendCodeBodiesHours;
            if (_status != null && !string.IsNullOrWhiteSpace(_viewModel.ImmediateWarning)) _status.Text = _viewModel.ImmediateWarning;
            UpdateToolsText();
        }

        private void UpdateLocalizedChrome()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateLocalizedChrome));
                return;
            }
            Title = Text("settings.window.title", "rvt-mcp · Settings");
            _tabItems[SettingsTab.General].Header = Text("settings.tab.general", "General");
            _tabItems[SettingsTab.Toast].Header = Text("settings.tab.toast", "Toast");
            // Tools is an invariant tab caption by contract.
            _tabItems[SettingsTab.Tools].Header = "Tools";
            _tabItems[SettingsTab.About].Header = Text("settings.tab.about", "About");
            if (_connectionHeading != null) _connectionHeading.Text = Text("settings.general.connection", "Connection");
            if (_portLabel != null) _portLabel.Text = Text("settings.general.port", "Port");
            if (_privacyHeading != null) _privacyHeading.Text = Text("settings.general.privacy", "Privacy");
            if (_cacheSendCodeBodies != null) _cacheSendCodeBodies.Content = Text("settings.general.cacheBodies", "Cache send_code bodies");
            if (_persistSendCodeBodies != null) _persistSendCodeBodies.Content = Text("settings.general.keepJournal", "Keep send_code journal");
            if (_journalLabel != null) _journalLabel.Text = Text("settings.general.journalDuration", "Journal duration (hours)");
            if (_copyPort != null) _copyPort.Content = Text("settings.general.copy", "Copy");
            if (_toastHeading != null) _toastHeading.Text = Text("settings.toast.heading", "Toast");
            if (_toastEnabled != null) _toastEnabled.Content = Text("settings.toast.enabled", "Show activity notifications");
            if (_toastIdleLabel != null) _toastIdleLabel.Text = Text("settings.toast.idle", "Idle duration (applies on the next activity)");
            if (_toastHelp != null) _toastHelp.Text = Text("settings.toast.help", "Toast On/Off is immediate. Apply saves the duration and privacy settings.");
            if (_aboutDescription != null) _aboutDescription.Text = Text("settings.about.description", "MCP connectivity and activity tools for Autodesk Revit.");
            if (_aboutHint != null) _aboutHint.Text = Text("settings.about.hint", "Version and license information are available in this tab.");
            if (_aboutLicense != null) _aboutLicense.Content = Text("settings.about.viewLicense", "View license");
            if (_aboutRepository != null) _aboutRepository.Content = Text("settings.about.github", "GitHub");
            if (_aboutDocumentation != null) _aboutDocumentation.Content = Text("settings.about.docs", "Documentation");
            if (_aboutIssues != null) _aboutIssues.Content = Text("settings.about.issues", "Report a bug");
            if (_toolsRefresh != null) _toolsRefresh.Content = Text("settings.tools.refresh", "Refresh");
            if (_toolsGrid != null && _toolsGrid.Columns.Count >= 5)
            {
                _toolsGrid.Columns[0].Header = Text("settings.tools.no", "No.");
                _toolsGrid.Columns[1].Header = Text("settings.tools.name", "Tool Name");
                _toolsGrid.Columns[2].Header = Text("settings.tools.description", "Description");
                _toolsGrid.Columns[3].Header = Text("settings.tools.source", "Source");
                _toolsGrid.Columns[4].Header = Text("settings.tools.timeout", "Time-out");
            }
            if (_apply != null) _apply.Content = Text("settings.apply", "Apply");
            if (_cancel != null) _cancel.Content = Text("settings.cancel", "Cancel");
            if (_close != null) _close.Content = Text("settings.close", "Close");
        }

        private static string Text(string key, string fallback)
        {
            var value = L.T(key);
            return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateDynamicText();
        }

        private void OnViewModelLanguageChanged(object sender, EventArgs e)
        {
            UpdateDynamicText();
            UpdateLocalizedChrome();
        }

        private void OnToolsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateToolsText();
        }

        private void UpdateToolsText()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateToolsText));
                return;
            }
            if (_toolsStatus == null || _toolsViewModel == null) return;
            if (_toolsCounts != null) _toolsCounts.Text = _toolsViewModel.CountsText;
            _toolsStatus.Text = _toolsViewModel.StatusText;
            _toolsStatus.ToolTip = _toolsViewModel.StatusHelpText;
            if (_toolsViewModel.StatusHelpText != null)
                AutomationProperties.SetHelpText(_toolsStatus, _toolsViewModel.StatusHelpText);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closing || !_viewModel.IsDirty) return;
            var answer = MessageBox.Show(this,
                "You have unapplied changes. Close without applying them?",
                "rvt-mcp Settings", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.LanguageChanged -= OnViewModelLanguageChanged;
            _toolsViewModel.PropertyChanged -= OnToolsPropertyChanged;
            PreviewKeyDown -= OnPreviewKeyDown;
            _viewModel.Dispose();
            _toolsViewModel.Dispose();
        }
    }
}
