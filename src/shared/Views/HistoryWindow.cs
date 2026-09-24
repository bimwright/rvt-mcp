using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
        private readonly TextBlock _footerText;
        private readonly RowDefinition _detailRow;
        private readonly Border _detailBorder;
        private readonly GridSplitter _detailSplitter;
        private GridLength _lastDetailHeight = new GridLength(240);
        private readonly CommandDispatcher _dispatcher;
        private readonly McpEventHandler _eventHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent _externalEvent;
        private readonly Button _rerunButton;
        private Button _loadHistoryButton;
        private McpCallEntry _selectedEntry;
        private Dictionary<string, string> _journalBodyCache;
        // Relocalizable chrome (ApplyLocalization rewrites these on L.Changed)
        private readonly DataGridColumn _colTime;
        private readonly DataGridColumn _colTool;
        private readonly DataGridColumn _colSummary;
        private readonly DataGridColumn _colStatus;
        private TextBlock _searchLabel;
        private TextBlock _filterLabel;
        private TextBlock _kindLabel;
        private TextBlock _whatSection;
        private TextBlock _inputSection;
        private TextBlock _outputSection;
        private Button _logsButton;
        private Button _newSessionButton;
        private int? _pastLoaded;
        private bool _rerunInProgress;

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
            // dispatcher. The window is hidden, never closed, so one subscription
            // covers its whole lifetime.
            L.Changed += (s, e) =>
            {
                var d = Dispatcher;
                if (d == null || d.HasShutdownStarted) return;
                if (d.CheckAccess()) ApplyLocalization();
                else d.BeginInvoke(new Action(ApplyLocalization));
            };
            Width = 900;
            Height = 600;
            MinWidth = 720;
            MinHeight = 420;
            MaxWidth = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            Resources.MergedDictionaries.Add(BimwrightStyles.Dictionary);
            ScrollBarFadeBehavior.SetIsEnabled(this, true);

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _detailRow = new RowDefinition { Height = new GridLength(0) };
            mainGrid.RowDefinitions.Add(_detailRow);

            // Row 0: Toolbar
            var toolbar = CreateToolbar();
            Grid.SetRow(toolbar, 0);
            mainGrid.Children.Add(toolbar);

            // Row 1: DataGrid
            _viewSource = new CollectionViewSource { Source = _sessionLog.Entries };
            _viewSource.Filter += OnFilter;

            _grid = new DataGrid
            {
                IsReadOnly = true,
                AutoGenerateColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                ItemsSource = _viewSource.View,
                Margin = new Thickness(4)
            };
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            _grid.ColumnHeaderStyle = headerStyle;

            var centerCell = new Style(typeof(TextBlock));
            centerCell.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));

            _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding("Index"), Width = 40, ElementStyle = centerCell });
            _colTime = new DataGridTextColumn { Binding = new Binding("TimeLabel"), Width = 92, ElementStyle = centerCell };
            _grid.Columns.Add(_colTime);
            _colTool = new DataGridTextColumn { Binding = new Binding("ToolName"), Width = 160 };
            _grid.Columns.Add(_colTool);
            _colSummary = new DataGridTextColumn
            {
                Binding = new Binding("Summary"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                ElementStyle = CreateTrimStyle()
            };
            _grid.Columns.Add(_colSummary);
            _grid.Columns.Add(new DataGridTextColumn { Header = "ms", Binding = new Binding("DurationMs"), Width = 55, ElementStyle = centerCell });
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
                Width = 50,
                ElementStyle = statusCell
            };
            _grid.Columns.Add(_colStatus);
            _grid.SelectionChanged += OnSelectionChanged;

            Grid.SetRow(_grid, 1);
            mainGrid.Children.Add(_grid);

            // Row 2: Detail panel — collapsed until a row is selected
            _detailBorder = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = Brushes.LightGray,
                Margin = new Thickness(4),
                Visibility = Visibility.Collapsed
            };
            var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _detailPanel = new Grid { Margin = new Thickness(4) };
            _detailPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _detailPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var r = 0; r < 6; r++)
                _detailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void Add(UIElement el, int row, int col)
            {
                Grid.SetRow(el, row);
                Grid.SetColumn(el, col);
                _detailPanel.Children.Add(el);
            }
            TextBlock SectionLabel() => new TextBlock
            {
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 10, 0)
            };

            // WHAT section
            var whatHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            _whatName = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 14 };
            _rerunButton = new Button
            {
                Style = (Style)FindResource(BimwrightStyles.PrimaryButtonKey),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsEnabled = false
            };
            _rerunButton.Click += OnRerunClick;
            DockPanel.SetDock(_rerunButton, Dock.Right);
            whatHeader.Children.Add(_rerunButton);
            whatHeader.Children.Add(_whatName);
            _whatDesc = new TextBlock { FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.Wrap };
            _warningLabel = new TextBlock { Foreground = Brushes.OrangeRed, FontSize = 11, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
            _whatSection = SectionLabel();
            Add(_whatSection, 0, 0);
            Add(whatHeader, 0, 1);
            Add(_whatDesc, 1, 1);
            Add(_warningLabel, 2, 1);

            // INPUT section
            _inputContent = new ContentControl { Margin = new Thickness(0, 0, 0, 8) };
            _inputSection = SectionLabel();
            Add(_inputSection, 3, 0);
            Add(_inputContent, 3, 1);

            // OUTPUT section
            _outputContainer = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            _outputSection = SectionLabel();
            Add(_outputSection, 4, 0);
            Add(_outputContainer, 4, 1);

            // Footer
            _footerText = new TextBlock { Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 8, 0, 0) };
            Add(_footerText, 5, 1);

            detailScroll.Content = _detailPanel;
            _detailBorder.Child = detailScroll;
            Grid.SetRow(_detailBorder, 2);
            mainGrid.Children.Add(_detailBorder);

            // Splitter between grid and detail
            _detailSplitter = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brushes.Transparent,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(_detailSplitter, 1);
            mainGrid.Children.Add(_detailSplitter);

            Content = mainGrid;
            ApplyLocalization();
        }

        /// <summary>
        /// Re-apply every localized string after an L.Changed swap: chrome labels,
        /// combo contents (stable Tag survives), the summary column (recomputed
        /// from in-memory ParamsJson/ResultJson), and the open detail pane.
        /// </summary>
        private void ApplyLocalization()
        {
            Title = L.T("history.window.title", ("brand", BrandAssets.Wordmark));
            _colTime.Header = L.T("history.col.time");
            _colTool.Header = L.T("history.col.tool");
            _colSummary.Header = L.T("history.col.summary");
            _colStatus.Header = L.T("history.col.status");
            _searchLabel.Text = L.T("history.toolbar.search");
            _filterLabel.Text = L.T("history.toolbar.filter");
            _kindLabel.Text = L.T("history.toolbar.kind");
            _logsButton.Content = L.T("history.toolbar.openLogs");
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
            foreach (var entry in _sessionLog.Entries)
                McpSessionLog.RefreshSummary(entry);
            _viewSource.View.Refresh();
            ShowEntry(_selectedEntry);
        }

        private StackPanel CreateToolbar()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4)
            };

            _searchLabel = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            panel.Children.Add(_searchLabel);

            _searchBox = new TextBox
            {
                Width = 150,
                Height = 24,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _searchBox.TextChanged += (s, e) => _viewSource.View.Refresh();
            panel.Children.Add(_searchBox);

            _filterLabel = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            panel.Children.Add(_filterLabel);

            // Items carry a stable Tag — display text is localized, filter
            // logic compares the tag, never the translated string.
            _filterCombo = new ComboBox { Width = 90, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var tag in new[] { "all", "success", "failed" })
                _filterCombo.Items.Add(new ComboBoxItem { Tag = tag });
            _filterCombo.SelectedIndex = 0;
            _filterCombo.SelectionChanged += (s, e) => _viewSource.View.Refresh();
            panel.Children.Add(_filterCombo);

            _kindLabel = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            panel.Children.Add(_kindLabel);

            _kindCombo = new ComboBox { Width = 70, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var tag in new[] { "all", "read", "write" })
                _kindCombo.Items.Add(new ComboBoxItem { Tag = tag });
            _kindCombo.SelectedIndex = 0;
            _kindCombo.SelectionChanged += (s, e) => _viewSource.View.Refresh();
            panel.Children.Add(_kindCombo);

            var toolbarStyle = (Style)FindResource(BimwrightStyles.ToolbarButtonKey);

            _logsButton = new Button { Style = toolbarStyle };
            _logsButton.Click += (s, e) => OpenLogFolder();
            panel.Children.Add(_logsButton);

            _loadHistoryButton = new Button { Style = toolbarStyle };
            _loadHistoryButton.Click += (s, e) => LoadPastSessions();
            panel.Children.Add(_loadHistoryButton);

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
            panel.Children.Add(_newSessionButton);

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
                    "RvtMcp");

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
                "RvtMcp");
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        }

        private void SetDetailVisible(bool visible)
        {
            if (!visible && _detailRow.Height.Value > 0)
                _lastDetailHeight = _detailRow.Height; // keep user-resized height
            _detailRow.MinHeight = visible ? 120 : 0; // detail is secondary — grid gets priority
            _detailRow.Height = visible ? _lastDetailHeight : new GridLength(0);
            _detailBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _detailSplitter.Visibility = _detailBorder.Visibility;
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
            if (entry == null)
            {
                SetDetailVisible(false);
                _whatName.Text = "";
                _whatDesc.Text = L.T("history.detail.selectPrompt");
                _warningLabel.Visibility = Visibility.Collapsed;
                _inputContent.Content = null;
                _outputContainer.Children.Clear();
                _footerText.Text = "";
                return;
            }

            SetDetailVisible(true);

            // WHAT
            _whatName.Text = entry.ToolName;
            _whatDesc.Text = entry.ToolDescription ?? "";
            _warningLabel.Foreground = Brushes.OrangeRed;
            if (entry.IsHistorical)
            {
                _warningLabel.Text = L.T("history.detail.historicalNote");
                _warningLabel.Foreground = Brushes.Gray;
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ToolName == "send_code_to_revit" && string.IsNullOrEmpty(entry.CodeSnippet))
            {
                _warningLabel.Text = IsRerunPossible(entry)
                    ? L.T("security.send_code.journal_redacted_rerun")
                    : L.T("security.send_code.redacted_no_rerun");
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ParamsTruncated)
            {
                _warningLabel.Text = L.T("security.send_code.params_truncated");
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ToolName == "send_code_to_revit")
            {
                _warningLabel.Text = L.T("security.send_code.execute_warning");
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
            _footerText.Text = $"{entry.DurationMs}ms · {(entry.Success ? L.T("history.footer.ok") : L.T("history.footer.fail"))} · {entry.TimeLabel}{rerunNote}{sessionNote}";
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
                    Text = sb.ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
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
                return new TextBlock { Text = L.T("history.input.noParams"), Foreground = Brushes.Gray };

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
                    Text = sb.ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };
            }
            catch
            {
                return new TextBlock { Text = entry.ParamsJson, TextWrapping = TextWrapping.Wrap };
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
                    Text = entry.ErrorMessage ?? L.T("history.output.unknownError"),
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
                            cellStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
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
                        Text = sb.ToString(),
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
                    Text = entry.ResultJson,
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
                    Text = L.T("history.rerun.resultHeader", ("time", DateTime.Now.ToString("HH:mm:ss"))),
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
                                        Text = L.T("history.rerun.diffLine",
                                            ("name", prop.Name), ("value", newVal),
                                            ("old", oldVal), ("delta", sign + delta.ToString())),
                                        Foreground = Brushes.DarkCyan,
                                        FontFamily = new FontFamily("Consolas"),
                                        FontSize = 11
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

                _footerText.Text = $"{sw.ElapsedMilliseconds}ms · {(result.Success ? L.T("history.footer.ok") : L.T("history.footer.fail"))}{L.T("history.footer.rerunOf", ("index", originalIndex))}";
            }
            catch (Exception ex)
            {
                _outputContainer.Children.Clear();
                _outputContainer.Children.Add(new TextBlock
                {
                    Text = L.T("history.rerun.failed", ("message", ex.Message)),
                    Foreground = Brushes.Red,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            finally
            {
                _rerunInProgress = false;
                _rerunButton.Content = L.T("history.rerun.button");
                _rerunButton.IsEnabled = true;
            }
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
}
