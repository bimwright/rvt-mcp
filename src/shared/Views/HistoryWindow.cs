using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin.Views
{
    public class HistoryWindow : Window
    {
        private readonly McpSessionLog _sessionLog;
        private readonly DataGrid _grid;
        private TextBox _searchBox;
        private ComboBox _filterCombo;
        private ComboBox _kindCombo;
        private readonly CollectionViewSource _viewSource;

        // Detail panel fields
        private readonly Grid _detailPanel;
        private readonly TextBlock _whatName;
        private readonly TextBlock _whatDesc;
        private readonly TextBlock _warningLabel;
        private readonly ContentControl _inputContent;
        private readonly StackPanel _outputContainer;
        private readonly TextBlock _shellStatus = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 12, 0)
        };
        private string _activityStatus = "";
        private Brush _activityBrush = Brushes.DimGray;
        private readonly CommandDispatcher _dispatcher;
        private readonly McpEventHandler _eventHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent _externalEvent;
        private readonly Button _rerunButton;
        private Button _loadHistoryButton;
        private McpCallEntry _selectedEntry;
        private Dictionary<string, string> _journalBodyCache;
        private readonly EventHandler _lChangedHandler;

        // Relocalizable chrome (ApplyLocalization rewrites these on L.Changed)
        private readonly DataGridColumn _colTime;
        private readonly DataGridColumn _colTool;
        private readonly DataGridColumn _colKind;
        private readonly DataGridColumn _colSeconds;
        private readonly DataGridColumn _colStatus;
        private TextBlock _searchPrompt;
        private GroupBox _activityGroup;
        private GroupBox _detailGroup;
        private TextBlock _whatSection;
        private TextBlock _inputSection;
        private TextBlock _outputSection;
        private Border _inputCard;
        private Border _outputCard;
        private Button _logsButton;
        private Button _newSessionButton;
        private Button _closeButton;
        private int? _pastLoaded;
        private bool _rerunInProgress;
        private bool _detailShowsRerun;
        private int _lastSeenL10nVersion = -1;
        private readonly TabControl _tabs;
        private readonly TabItem _activityTab;
        private readonly TabItem _issuesTab;
        private readonly FailureIssuesView _issuesView;

        private static readonly Dictionary<string, string> FilterKeys = new Dictionary<string, string>
        {
            ["all"] = "history.filter.all",
            ["success"] = "history.filter.success",
            ["failed"] = "history.filter.failed"
        };
        private static readonly Dictionary<string, string> KindKeys = new Dictionary<string, string>
        {
            ["all"] = "history.filter.all",
            ["read"] = "history.kind.read",
            ["write"] = "history.kind.write"
        };

        public HistoryWindow(McpSessionLog sessionLog, CommandDispatcher dispatcher,
                             McpEventHandler eventHandler, Autodesk.Revit.UI.ExternalEvent externalEvent)
        {
            _sessionLog = sessionLog;
            _dispatcher = dispatcher;
            _eventHandler = eventHandler;
            _externalEvent = externalEvent;

            Title = L.T("history.window.title", ("brand", BrandAssets.Wordmark));
            // L.Changed may fire on the watcher thread — marshal to this window's
            // dispatcher. Subscribe on Loaded / unsubscribe on Unloaded: Hide()
            // unloads the window and App.cs then builds a NEW instance on reopen,
            // so a ctor-time subscription would root every discarded window.
            _lChangedHandler = (s, e) =>
            {
                var d = Dispatcher;
                if (d == null || d.HasShutdownStarted) return;
                if (d.CheckAccess()) ApplyLocalization();
                else d.BeginInvoke(new Action(ApplyLocalization));
            };
            Loaded += (s, e) =>
            {
                L.Changed += _lChangedHandler;
                ApplyLocalization();   // catch up on swaps that happened while hidden
            };
            Unloaded += (s, e) => L.Changed -= _lChangedHandler;
            var area = SystemParameters.WorkArea;
            MinWidth = Math.Min(960, area.Width);
            MinHeight = Math.Min(640, area.Height);
            MaxWidth = Math.Max(MinWidth, Math.Min(1400, area.Width));
            MaxHeight = Math.Max(MinHeight, Math.Min(900, area.Height));
            Width = Math.Min(Math.Max(1100, MinWidth), MaxWidth);
            Height = Math.Min(Math.Max(720, MinHeight), MaxHeight);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            Resources.MergedDictionaries.Add(BimwrightStyles.Dictionary);
            Resources.MergedDictionaries.Add(HistoryChrome.TabDictionary());
            ScrollBarFadeBehavior.SetIsEnabled(this, true);

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = CreateToolbar();
            Grid.SetRow(toolbar, 0);
            mainGrid.Children.Add(toolbar);

            // Row 1: DataGrid
            _viewSource = new CollectionViewSource { Source = _sessionLog.Entries };
            _viewSource.Filter += OnFilter;
            _viewSource.SortDescriptions.Add(new SortDescription(nameof(McpCallEntry.Timestamp), ListSortDirection.Descending));
            _viewSource.SortDescriptions.Add(new SortDescription(nameof(McpCallEntry.Index), ListSortDirection.Descending));

            _grid = new DataGrid
            {
                IsReadOnly = true,
                AutoGenerateColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                ItemsSource = _viewSource.View,
                Margin = new Thickness(0)
            };
            BimwrightStyles.KeepSelectionHighlight(_grid);
            // Pixel units: HistoryChrome.Wheel steps in pixels; the DataGrid default (Item) would turn one notch into ~31 rows.
            VirtualizingPanel.SetScrollUnit(_grid, ScrollUnit.Pixel);
            _grid.PreviewMouseWheel += (s, e) =>
            {
                var viewer = HistoryChrome.FindScrollViewer(_grid);
                if (viewer != null) HistoryChrome.Wheel(viewer, e, 0.5);
            };
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            _grid.ColumnHeaderStyle = headerStyle;

            var centerCell = new Style(typeof(TextBlock));
            centerCell.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));

            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "#",
                Binding = new Binding("Index"),
                Width = new DataGridLength(0.5, DataGridLengthUnitType.Star),
                MinWidth = 40,
                ElementStyle = centerCell
            });
            _colTime = new DataGridTextColumn
            {
                Binding = new Binding("TimeLabel"),
                Width = new DataGridLength(1.3, DataGridLengthUnitType.Star),
                MinWidth = 96,
                ElementStyle = centerCell
            };
            _grid.Columns.Add(_colTime);
            _colTool = new DataGridTextColumn
            {
                Binding = new Binding("ToolName"),
                Width = new DataGridLength(2.4, DataGridLengthUnitType.Star),
                MinWidth = 120,
                ElementStyle = CreateTrimStyle()
            };
            _grid.Columns.Add(_colTool);
            _colKind = new DataGridTextColumn
            {
                Binding = new Binding("ToolName") { Converter = new ActivityKindConverter() },
                Width = new DataGridLength(0.8, DataGridLengthUnitType.Star),
                MinWidth = 64,
                ElementStyle = centerCell
            };
            _grid.Columns.Add(_colKind);
            _colSeconds = new DataGridTextColumn
            {
                Binding = new Binding("DurationMs") { Converter = new DurationSecondsConverter() },
                Width = new DataGridLength(0.7, DataGridLengthUnitType.Star),
                MinWidth = 48,
                ElementStyle = centerCell
            };
            _grid.Columns.Add(_colSeconds);
            // Status: ✓ green / ✗ red — glyph + colour, readable at a glance.
            var statusCell = new Style(typeof(TextBlock));
            statusCell.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            statusCell.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            statusCell.Setters.Add(new Setter(TextBlock.ForegroundProperty, FrozenBrush(0x38, 0xA1, 0x69)));
            var statusFail = new DataTrigger { Binding = new Binding("Success"), Value = false };
            statusFail.Setters.Add(new Setter(TextBlock.ForegroundProperty, FrozenBrush(0xE5, 0x3E, 0x3E)));
            statusCell.Triggers.Add(statusFail);

            _colStatus = new DataGridTextColumn
            {
                Binding = new Binding("Success") { Converter = new BoolToStatusConverter() },
                Width = new DataGridLength(0.7, DataGridLengthUnitType.Star),
                MinWidth = 56,
                ElementStyle = statusCell
            };
            _grid.Columns.Add(_colStatus);
            _grid.SelectionChanged += OnSelectionChanged;

            // Same inset and 5:6 split as FailureIssuesView, so the grid and detail match the Issues tab.
            var body = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 360 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star), MinWidth = 420 });
            _activityGroup = new GroupBox { Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8), Content = _grid };
            Grid.SetColumn(_activityGroup, 0);
            body.Children.Add(_activityGroup);
            var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            detailScroll.PreviewMouseWheel += (s, e) => HistoryChrome.Wheel(detailScroll, e, 0.26);
            var sectionHeader = FrozenBrush(0xF7, 0xF8, 0xFA);
            var sectionRule = FrozenBrush(0xE0, 0xE0, 0xE0);
            var sectionLabel = FrozenBrush(0x64, 0x74, 0x8B);
            Border SectionCard(TextBlock label, UIElement sectionBody, Thickness margin)
            {
                label.FontWeight = FontWeights.SemiBold;
                label.FontSize = 11;
                label.Foreground = sectionLabel;
                label.Margin = new Thickness(0);
                var header = new Border
                {
                    Background = sectionHeader,
                    BorderBrush = sectionRule,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = label
                };
                var content = new Border { Padding = new Thickness(8, 6, 8, 8), Child = sectionBody };
                var stack = new StackPanel();
                stack.Children.Add(header);
                stack.Children.Add(content);
                return new Border
                {
                    Background = Brushes.White,
                    BorderBrush = sectionRule,
                    BorderThickness = new Thickness(1),
                    SnapsToDevicePixels = true,
                    ClipToBounds = true,
                    Margin = margin,
                    Child = stack
                };
            }

            _detailPanel = new Grid();
            var sections = new StackPanel();
            _detailPanel.Children.Add(sections);

            _whatName = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
            _whatDesc = new TextBlock { FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            _warningLabel = new TextBlock { Foreground = Brushes.OrangeRed, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            var whatBody = new StackPanel();
            whatBody.Children.Add(_whatName);
            whatBody.Children.Add(_whatDesc);
            whatBody.Children.Add(_warningLabel);
            _whatSection = new TextBlock();
            sections.Children.Add(SectionCard(_whatSection, whatBody, new Thickness(0, 0, 0, 8)));

            _inputContent = new ContentControl();
            _inputSection = new TextBlock();
            _inputCard = SectionCard(_inputSection, _inputContent, new Thickness(0, 0, 0, 8));
            sections.Children.Add(_inputCard);

            _outputContainer = new StackPanel();
            _outputSection = new TextBlock();
            _outputCard = SectionCard(_outputSection, _outputContainer, new Thickness(0));
            sections.Children.Add(_outputCard);
            _rerunButton = new Button
            {
                Style = (Style)FindResource(BimwrightStyles.PrimaryButtonKey),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
                IsEnabled = false
            };
            _rerunButton.Click += OnRerunClick;
            sections.Children.Add(_rerunButton);

            detailScroll.Content = _detailPanel;
            _detailGroup = new GroupBox
            {
                Margin = new Thickness(4, 0, 0, 4),
                Padding = new Thickness(8),
                Content = detailScroll
            };
            Grid.SetColumn(_detailGroup, 1);
            body.Children.Add(_detailGroup);
            Grid.SetRow(body, 1);
            mainGrid.Children.Add(body);

            _issuesView = new FailureIssuesView();
            _activityTab = new TabItem { Content = mainGrid };
            _issuesTab = new TabItem { Content = _issuesView };
            _tabs = new TabControl();
            _tabs.Items.Add(_activityTab);
            _tabs.Items.Add(_issuesTab);
            _tabs.SelectionChanged += (s, e) => { if (ReferenceEquals(e.Source, _tabs)) ShowShellStatus(); };
            _issuesView.ShellStatusChanged += (s, e) => ShowShellStatus();
            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.Children.Add(_tabs);
            _closeButton = new Button { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _closeButton.Click += (s, e) => Close();
            var closeBarPanel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_closeButton, Dock.Right);
            closeBarPanel.Children.Add(_closeButton);
            closeBarPanel.Children.Add(_shellStatus);
            var closeBar = new Border
            {
                Background = FrozenBrush(0xF7, 0xF8, 0xFA),
                BorderBrush = FrozenBrush(0xE0, 0xE0, 0xE0),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 6, 8, 8),
                Child = closeBarPanel
            };
            Grid.SetRow(closeBar, 1);
            shell.Children.Add(closeBar);
            Content = shell;
            _lastSeenL10nVersion = L.Version;
            ApplyLocalization();
            if (_grid.SelectedItem == null)
                _grid.SelectedItem = _viewSource.View.Cast<object>().FirstOrDefault();
        }

        /// <summary>
        /// Re-apply every localized string after an L.Changed swap: chrome labels,
        /// combo contents (stable Tag survives), the summary column (recomputed
        /// from in-memory ParamsJson/ResultJson), and the open detail pane.
        /// </summary>
        private void ApplyLocalization()
        {
            var version = L.Version;
            var localeChanged = version != _lastSeenL10nVersion;
            _lastSeenL10nVersion = version;
            Title = L.T("history.window.title", ("brand", BrandAssets.Wordmark));
            _activityTab.Header = L.T("failure.tab.history");
            _issuesTab.Header = L.T("failure.tab.issues");
            _activityGroup.Header = L.T("failure.tab.history");
            _detailGroup.Header = L.T("history.group.detail");
            _colTime.Header = L.T("history.col.time");
            _colTool.Header = L.T("history.col.tool");
            _colKind.Header = L.T("history.col.kind");
            _colSeconds.Header = L.T("history.col.seconds");
            _colStatus.Header = L.T("history.col.status");
            _searchPrompt.Text = L.T("history.search.placeholder");
            _logsButton.Content = L.T("history.toolbar.openLogs");
            _closeButton.Content = L.T("update.close");
            _newSessionButton.Content = L.T("history.toolbar.newSession");
            _loadHistoryButton.Content = _pastLoaded == null
                ? L.T("history.toolbar.loadPast")
                : _pastLoaded == 0
                    ? L.T("history.toolbar.noPastEntries")
                    : L.T("history.toolbar.loadedPast", ("count", _pastLoaded.Value));
            foreach (ComboBoxItem item in _filterCombo.Items)
                item.Content = L.T(FilterKeys[(string)item.Tag]);
            foreach (ComboBoxItem item in _kindCombo.Items)
                item.Content = L.T(KindKeys[(string)item.Tag]);
            _whatSection.Text = L.T("history.section.what");
            _inputSection.Text = L.T("history.section.input");
            _outputSection.Text = L.T("history.section.output");
            _rerunButton.Content = L.T(_rerunInProgress ? "history.rerun.running" : "history.rerun.button");
            // Regenerating 1000 summaries parses JSON + runs regex — only worth it
            // when the table actually changed, and PreserveSummary entries are skipped.
            if (localeChanged)
            {
                foreach (var entry in _sessionLog.Entries)
                    McpSessionLog.RefreshSummary(entry);
                _viewSource.View.Refresh();
            }
            // Re-run results live in _outputContainer — a locale swap must not wipe them.
            if (!_rerunInProgress && !_detailShowsRerun)
                ShowEntry(_selectedEntry);
        }

        /// <summary>Called after the native callback has returned; never opens an approval inside failure processing.</summary>
        public void ShowIssues()
        {
            _tabs.SelectedItem = _issuesTab;
            if (_issuesView.IsLoaded) _issuesView.Refresh();
        }

        private DockPanel CreateToolbar()
        {
            var panel = new DockPanel { LastChildFill = false, Margin = new Thickness(8, 6, 8, 6) };
            var toolbarStyle = (Style)FindResource(BimwrightStyles.ToolbarButtonKey);
            var sessionActions = new StackPanel { Orientation = Orientation.Horizontal };

            _logsButton = new Button { Style = toolbarStyle };
            _logsButton.Click += (s, e) => OpenLogFolder();
            sessionActions.Children.Add(_logsButton);

            _loadHistoryButton = new Button { Style = toolbarStyle };
            _loadHistoryButton.Click += (s, e) => LoadPastSessions();
            sessionActions.Children.Add(_loadHistoryButton);

            _newSessionButton = new Button { Style = toolbarStyle };
            _newSessionButton.Click += (s, e) =>
            {
                var confirm = MessageBox.Show(
                    this,
                    L.T("history.newSession.confirm"),
                    L.T("history.newSession.title"),
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.OK) return;

                _sessionLog.Clear();
                ShowEntry(null);
            };
            sessionActions.Children.Add(_newSessionButton);
            DockPanel.SetDock(sessionActions, Dock.Right);
            panel.Children.Add(sessionActions);

            var filters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var searchHost = new Grid { Width = 220, Margin = new Thickness(0, 0, 8, 0) };
            _searchBox = new TextBox
            {
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 0, 6, 0)
            };
            _searchPrompt = new TextBlock
            {
                Style = (Style)BimwrightStyles.Dictionary[BimwrightStyles.PlaceholderTextKey],
                Margin = new Thickness(8, 0, 4, 0)
            };
            _searchBox.TextChanged += (s, e) =>
            {
                _searchPrompt.Visibility = string.IsNullOrEmpty(_searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                _viewSource?.View.Refresh();
            };
            searchHost.Children.Add(_searchBox);
            searchHost.Children.Add(_searchPrompt);
            filters.Children.Add(searchHost);

            // Items carry a stable Tag — display text is localized, filter
            // logic compares the tag, never the translated string.
            _kindCombo = new ComboBox { MinWidth = 110, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var tag in new[] { "all", "read", "write" })
                _kindCombo.Items.Add(new ComboBoxItem { Tag = tag });
            _kindCombo.SelectedIndex = 0;
            _kindCombo.SelectionChanged += (s, e) => _viewSource?.View.Refresh();
            filters.Children.Add(_kindCombo);

            _filterCombo = new ComboBox { MinWidth = 120, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var tag in new[] { "all", "success", "failed" })
                _filterCombo.Items.Add(new ComboBoxItem { Tag = tag });
            _filterCombo.SelectedIndex = 0;
            _filterCombo.SelectionChanged += (s, e) => _viewSource?.View.Refresh();
            filters.Children.Add(_filterCombo);
            DockPanel.SetDock(filters, Dock.Left);
            panel.Children.Add(filters);
            return panel;
        }

        private void OnFilter(object sender, FilterEventArgs e)
        {
            var entry = e.Item as McpCallEntry;
            if (entry == null) { e.Accepted = false; return; }

            var search = _searchBox?.Text;
            if (!string.IsNullOrEmpty(search) && !MatchesSearch(entry, search))
            {
                e.Accepted = false;
                return;
            }

            var filter = (_filterCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            if (filter == "success" && !entry.Success) { e.Accepted = false; return; }
            if (filter == "failed" && entry.Success) { e.Accepted = false; return; }

            var kind = (_kindCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            if (kind == "read" && ToolActivityClassifier.Classify(entry.ToolName) != ToolActivityKind.Read) { e.Accepted = false; return; }
            if (kind == "write" && ToolActivityClassifier.Classify(entry.ToolName) != ToolActivityKind.Write) { e.Accepted = false; return; }

            e.Accepted = true;
        }

        private static bool MatchesSearch(McpCallEntry entry, string search)
        {
            return Contains(entry.ToolName, search)
                || Contains(entry.Summary, search)
                || Contains(entry.ParamsJson, search)
                || Contains(entry.ErrorMessage, search);
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LoadPastSessions()
        {
            var logDir = !string.IsNullOrEmpty(McpLogger.CurrentLogPath)
                ? Path.GetDirectoryName(McpLogger.CurrentLogPath)
                : Path.Combine(
                    McpLogger.LocalAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bimwright", "rvt-mcp");

            var liveCount = _sessionLog.Entries.Count(e => !e.IsHistorical);
            var entries = SessionLogHistoryLoader.LoadPastSessions(logDir, McpLogger.CurrentSessionId, liveCount);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entry.ToolDescription = _dispatcher.GetCommand(entry.ToolName)?.Description;
                _sessionLog.Entries.Insert(i, entry);
            }

            _loadHistoryButton.IsEnabled = false;
            _pastLoaded = entries.Count;
            _loadHistoryButton.Content = entries.Count == 0
                ? L.T("history.toolbar.noPastEntries")
                : L.T("history.toolbar.loadedPast", ("count", entries.Count));
        }

        private static void OpenLogFolder()
        {
            var dir = Path.Combine(
                McpLogger.LocalAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "rvt-mcp");
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Guard: event fires during construction before detail panel fields exist
            if (_whatName == null) return;

            _selectedEntry = _grid.SelectedItem as McpCallEntry;
            _rerunButton.IsEnabled = IsRerunPossible(_selectedEntry);
            ShowEntry(_selectedEntry);
        }

        private void ShowEntry(McpCallEntry entry)
        {
            _detailShowsRerun = false;
            if (entry == null)
            {
                _whatName.Text = "";
                _whatDesc.Text = L.T("history.detail.selectPrompt");
                _warningLabel.Visibility = Visibility.Collapsed;
                _inputContent.Content = null;
                _outputContainer.Children.Clear();
                _inputCard.Visibility = Visibility.Collapsed;
                _outputCard.Visibility = Visibility.Collapsed;
                SetActivityStatus("", Brushes.DimGray);
                return;
            }

            _inputCard.Visibility = Visibility.Visible;
            _outputCard.Visibility = Visibility.Visible;

            // WHAT
            _whatName.Text = SoftWrap(entry.ToolName);
            _whatDesc.Text = SoftWrap(entry.ToolDescription ?? "");
            _warningLabel.Foreground = Brushes.OrangeRed;
            if (entry.IsHistorical)
            {
                _warningLabel.Text = SoftWrap(L.T("history.detail.historicalNote"));
                _warningLabel.Foreground = Brushes.Gray;
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ToolName == "send_code_to_revit" && string.IsNullOrEmpty(entry.CodeSnippet))
            {
                _warningLabel.Text = SoftWrap(IsRerunPossible(entry)
                    ? L.T("security.send_code.journal_redacted_rerun")
                    : L.T("security.send_code.redacted_no_rerun"));
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ParamsTruncated)
            {
                _warningLabel.Text = SoftWrap(L.T("security.send_code.params_truncated"));
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ToolName == "send_code_to_revit")
            {
                _warningLabel.Text = SoftWrap(L.T("security.send_code.execute_warning"));
                _warningLabel.Visibility = Visibility.Visible;
            }
            else
            {
                _warningLabel.Visibility = Visibility.Collapsed;
            }

            // INPUT
            _inputContent.Content = BuildInputControl(entry);

            // OUTPUT
            _outputContainer.Children.Clear();
            FormatOutput(entry, _outputContainer);

            // Footer
            var rerunNote = entry.RerunOfIndex.HasValue
                ? L.T("history.footer.rerunOf", ("index", entry.RerunOfIndex.Value))
                : "";
            var sessionNote = entry.IsHistorical
                ? L.T("history.footer.session", ("tag", entry.SessionTag))
                : "";
            SetActivityStatus($"{entry.DurationMs}ms · {(entry.Success ? L.T("history.footer.ok") : L.T("history.footer.fail"))} · {entry.TimeLabel}{rerunNote}{sessionNote}",
                entry.Success ? Brushes.DimGray : Brushes.Firebrick);
        }

        private void SetActivityStatus(string text, Brush brush)
        {
            _activityStatus = text ?? "";
            _activityBrush = brush ?? Brushes.DimGray;
            ShowShellStatus();
        }

        private void ShowShellStatus()
        {
            if (_shellStatus == null || _tabs == null) return;
            if (_tabs.SelectedItem == _issuesTab)
                ApplyShellStatus(_issuesView.ShellStatusText, _issuesView.ShellStatusBrush);
            else
                ApplyShellStatus(_activityStatus, _activityBrush);
        }

        private void ApplyShellStatus(string text, Brush brush)
        {
            _shellStatus.Text = text ?? "";
            _shellStatus.Foreground = brush ?? Brushes.DimGray;
            if (string.IsNullOrEmpty(_shellStatus.Text)) _shellStatus.ToolTip = null;
            else HistoryChrome.Guide(_shellStatus, _shellStatus.Text);
        }

        private UIElement BuildInputControl(McpCallEntry entry)
        {
            if (entry.ToolName == "send_code_to_revit" && !string.IsNullOrEmpty(entry.CodeSnippet))
            {
                var lines = entry.CodeSnippet.Split('\n');
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                    sb.AppendLine($"{i + 1,3} | {lines[i]}");

                var codeBox = new TextBox
                {
                    // Layout wrapping must not add characters to selectable source text.
                    Text = sb.ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = 250,
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.LightGray,
                    Background = new SolidColorBrush(Color.FromRgb(245, 245, 245))
                };

                if (lines.Length > 15)
                {
                    codeBox.MaxHeight = 300;
                    return new Expander
                    {
                        Header = L.T("history.input.codeLines", ("lines", lines.Length)),
                        IsExpanded = false,
                        Content = codeBox
                    };
                }
                return codeBox;
            }

            if (string.IsNullOrEmpty(entry.ParamsJson))
                return new TextBlock { Text = L.T("history.input.noParams"), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };

            try
            {
                var obj = JObject.Parse(entry.ParamsJson);
                var sb = new System.Text.StringBuilder();
                string sqlValue = null;
                foreach (var prop in obj.Properties())
                {
                    var val = prop.Value.ToString();
                    if (prop.Name == "sql" && val.Length > 40)
                    {
                        sqlValue = val;
                        sb.AppendLine($"{prop.Name}:");
                        sb.AppendLine(FormatSql(val));
                    }
                    else
                    {
                        sb.AppendLine($"{prop.Name}: {Truncate(val, 200)}");
                    }
                }

                if (sqlValue != null)
                {
                    return new TextBox
                    {
                        Text = sb.ToString(),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        BorderThickness = new Thickness(1),
                        BorderBrush = Brushes.LightGray,
                        Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                        MaxHeight = 200
                    };
                }
                return new TextBlock
                {
                    Text = SoftWrap(sb.ToString()),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };
            }
            catch
            {
                return new TextBlock { Text = SoftWrap(entry.ParamsJson), TextWrapping = TextWrapping.Wrap };
            }
        }

        private static string FormatSql(string sql)
        {
            var keywords = new[] { "SELECT ", "FROM ", "LEFT JOIN ", "INNER JOIN ", "JOIN ",
                                   "WHERE ", "GROUP BY ", "ORDER BY ", "HAVING ", "LIMIT ", "UNION " };
            foreach (var kw in keywords)
                sql = sql.Replace(kw, "\n  " + kw);
            return sql.TrimStart('\n');
        }

        internal void FormatOutput(McpCallEntry entry, StackPanel container)
        {
            if (!entry.Success)
            {
                container.Children.Add(new TextBlock
                {
                    Text = SoftWrap(entry.ErrorMessage ?? L.T("history.output.unknownError")),
                    Foreground = Brushes.Red,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            if (string.IsNullOrEmpty(entry.ResultJson))
            {
                container.Children.Add(new TextBlock { Text = L.T("history.output.noOutput"), Foreground = Brushes.Gray });
                return;
            }

            try
            {
                var result = JObject.Parse(entry.ResultJson);
                var rows = result["rows"] as JArray;

                if (rows != null && rows.Count > 0)
                {
                    var tableGrid = new DataGrid
                    {
                        IsReadOnly = true,
                        AutoGenerateColumns = false,
                        MaxHeight = 200,
                        FontSize = 11,
                        HeadersVisibility = DataGridHeadersVisibility.Column,
                        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                        CanUserResizeColumns = true,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                    };

                    var firstRow = rows[0] as JObject;
                    if (firstRow != null)
                    {
                        foreach (var col in firstRow.Properties())
                        {
                            var binding = new Binding($"[{col.Name}]");
                            var column = new DataGridTextColumn { Header = col.Name, Binding = binding, MaxWidth = 250 };
                            var cellStyle = new Style(typeof(TextBlock));
                            cellStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                            cellStyle.Setters.Add(new Setter(ToolTipProperty, new Binding($"[{col.Name}]")));
                            column.ElementStyle = cellStyle;
                            tableGrid.Columns.Add(column);
                        }
                    }

                    var items = new List<Dictionary<string, string>>();
                    int maxRows = Math.Min(rows.Count, 10);
                    for (int i = 0; i < maxRows; i++)
                    {
                        var row = rows[i] as JObject;
                        if (row == null) continue;
                        var dict = new Dictionary<string, string>();
                        foreach (var prop in row.Properties())
                            dict[prop.Name] = prop.Value.ToString();
                        items.Add(dict);
                    }
                    tableGrid.ItemsSource = items;
                    container.Children.Add(tableGrid);

                    if (rows.Count > 10)
                    {
                        container.Children.Add(new TextBlock
                        {
                            Text = L.T("history.output.showingRows", ("shown", 10), ("total", rows.Count)),
                            Foreground = Brushes.Gray,
                            FontSize = 10,
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var prop in result.Properties())
                    {
                        if (prop.Name == "rows") continue;
                        sb.AppendLine($"{prop.Name}: {Truncate(prop.Value.ToString(), 200)}");
                    }
                    container.Children.Add(new TextBlock
                    {
                        Text = SoftWrap(sb.ToString()),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    });
                }
            }
            catch
            {
                container.Children.Add(new TextBlock
                {
                    Text = SoftWrap(entry.ResultJson),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        private bool IsRerunPossible(McpCallEntry entry)
        {
            if (entry == null || entry.IsHistorical || entry.ParamsTruncated)
                return false;
            // send_code bodies are redacted to {code_hash, code_length} unless
            // CacheSendCodeBodies is on — fall back to the send-code journal.
            if (entry.ToolName == "send_code_to_revit" && string.IsNullOrEmpty(entry.CodeSnippet))
                return ResolveRedactedSendCodeBody(entry) != null;
            return true;
        }

        /// <summary>Journal body for a redacted send_code entry, or null (cached per hash).</summary>
        private string ResolveRedactedSendCodeBody(McpCallEntry entry)
        {
            try
            {
                var hash = JObject.Parse(entry.ParamsJson)?.Value<string>("code_hash");
                if (string.IsNullOrEmpty(hash)) return null;
                if (_journalBodyCache == null)
                    _journalBodyCache = new Dictionary<string, string>(StringComparer.Ordinal);
                if (_journalBodyCache.TryGetValue(hash, out var cached))
                    return cached;
                var body = SendCodeJournal.TryFindCodeByHash(hash);
                _journalBodyCache[hash] = body;
                return body;
            }
            catch { return null; }
        }

        private async void OnRerunClick(object sender, RoutedEventArgs e)
        {
            if (!IsRerunPossible(_selectedEntry)) return;

            string paramsOverride = null;
            if (_selectedEntry.ToolName == "send_code_to_revit")
            {
                var redacted = string.IsNullOrEmpty(_selectedEntry.CodeSnippet);
                if (redacted)
                {
                    var body = ResolveRedactedSendCodeBody(_selectedEntry);
                    if (body == null) return; // IsRerunPossible already gated; defensive
                    paramsOverride = new JObject { ["code"] = body }
                        .ToString(Newtonsoft.Json.Formatting.None);
                }
                var prompt = redacted
                    ? L.T("security.send_code.rerunConfirmRedacted")
                    : L.T("security.send_code.rerunConfirm");
                var confirm = MessageBox.Show(
                    this,
                    prompt,
                    L.T("security.send_code.rerunConfirmTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            _rerunInProgress = true;
            _rerunButton.Content = L.T("history.rerun.running");
            _rerunButton.IsEnabled = false;

            try
            {
                var toolName = _selectedEntry.ToolName;
                var paramsJson = paramsOverride ?? _selectedEntry.ParamsJson;
                var originalIndex = _selectedEntry.Index;
                var originalResultJson = _selectedEntry.ResultJson;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await System.Threading.Tasks.Task.Run(async () =>
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
                    var request = new PendingRequest
                    {
                        Id = Guid.NewGuid().ToString(),
                        CommandName = toolName,
                        ParamsJson = paramsJson,
                        Tcs = tcs
                    };
                    _eventHandler.Enqueue(request);
                    _externalEvent.Raise();

                    var responseJson = await tcs.Task;
                    var response = JObject.Parse(responseJson);
                    bool success = response.Value<bool>("success");
                    string error = response.Value<string>("error");
                    object data = response["data"]?.ToObject<object>();
                    return new CommandResult
                    {
                        Success = success,
                        Error = error,
                        Data = data
                    };
                });

                sw.Stop();

                string resultJson = null;
                try { resultJson = result.Data != null ? Newtonsoft.Json.JsonConvert.SerializeObject(result.Data) : null; }
                catch { }
                string sessionResult = resultJson != null && resultJson.Length > 10240
                    ? resultJson.Substring(0, 10240) : resultJson;

                var cmd = _dispatcher.GetCommand(toolName);
                _sessionLog.Add(new McpCallEntry
                {
                    ToolName = toolName,
                    ParamsJson = paramsJson,
                    Success = result.Success,
                    DurationMs = sw.ElapsedMilliseconds,
                    ErrorMessage = result.Error,
                    ResultJson = sessionResult,
                    ToolDescription = cmd?.Description,
                    Summary = SummaryGenerator.Generate(toolName, paramsJson,
                                                         sessionResult, result.Success, result.Error),
                    RerunOfIndex = originalIndex
                });

                // Show re-run result in output panel
                _outputContainer.Children.Clear();
                _outputContainer.Children.Add(new TextBlock
                {
                    Text = L.T("history.rerun.resultHeader", ("time", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4)
                });

                // Diff hints for numeric fields
                if (originalResultJson != null && sessionResult != null)
                {
                    try
                    {
                        var oldResult = JObject.Parse(originalResultJson);
                        var newResult = JObject.Parse(sessionResult);
                        foreach (var prop in newResult.Properties())
                        {
                            if (prop.Value.Type == JTokenType.Integer &&
                                oldResult[prop.Name]?.Type == JTokenType.Integer)
                            {
                                var oldVal = oldResult[prop.Name].Value<long>();
                                var newVal = prop.Value.Value<long>();
                                if (oldVal != newVal)
                                {
                                    var delta = newVal - oldVal;
                                    var sign = delta > 0 ? "+" : "";
                                    _outputContainer.Children.Add(new TextBlock
                                    {
                                        Text = SoftWrap(L.T("history.rerun.diffLine",
                                            ("name", prop.Name), ("value", newVal),
                                            ("old", oldVal), ("delta", sign + delta.ToString()))),
                                        Foreground = Brushes.DarkCyan,
                                        FontFamily = new FontFamily("Consolas"),
                                        FontSize = 11,
                                        TextWrapping = TextWrapping.Wrap
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }

                var rerunEntry = new McpCallEntry
                {
                    Success = result.Success,
                    ResultJson = sessionResult,
                    ErrorMessage = result.Error,
                    ToolName = toolName
                };
                FormatOutput(rerunEntry, _outputContainer);

                SetActivityStatus($"{sw.ElapsedMilliseconds}ms · {(result.Success ? L.T("history.footer.ok") : L.T("history.footer.fail"))}{L.T("history.footer.rerunOf", ("index", originalIndex))}",
                    result.Success ? Brushes.DimGray : Brushes.Firebrick);
            }
            catch (Exception ex)
            {
                _outputContainer.Children.Clear();
                _outputContainer.Children.Add(new TextBlock
                {
                    Text = SoftWrap(L.T("history.rerun.failed", ("message", ex.Message))),
                    Foreground = Brushes.Red,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            finally
            {
                _rerunInProgress = false;
                _detailShowsRerun = true;
                _rerunButton.Content = L.T("history.rerun.button");
                _rerunButton.IsEnabled = true;
            }
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

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max - 3) + "...";
        }

        private static Style CreateTrimStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            return style;
        }

        private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }

    internal class BoolToStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is bool b && b ? "✓" : "✗";

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    internal class ActivityKindConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var kind = ToolActivityClassifier.Classify(value as string);
            return L.T(kind == ToolActivityKind.Write ? "history.kind.write" : "history.kind.read");
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    internal class DurationSecondsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var ms = value is long l ? l : 0L;
            return (ms / 1000d).ToString("0.0", culture ?? System.Globalization.CultureInfo.CurrentCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}
