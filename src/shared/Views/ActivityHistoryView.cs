using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views.Toast;
using RvtMcp.Plugin.Presentation.History;

namespace RvtMcp.Plugin.Views
{
    public sealed class ActivityHistoryView : UserControl, IDisposable
    {
        private readonly ActivityHistoryViewModel _vm;
        private bool _observing;
        private bool _disposed;
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
        private readonly Button _rerunButton;
        private Button _loadHistoryButton;
        private McpCallEntry _selectedEntry => _vm.SelectedEntry;

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
        private bool _rerunInProgress => _vm.RerunCommand.IsRunning;
        private bool _detailShowsRerun;
        private int _lastSeenL10nVersion = -1;

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

        public ActivityHistoryView(ActivityHistoryViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            DataContext = vm;
            Resources.MergedDictionaries.Add(BimwrightStyles.Dictionary);
            ScrollBarFadeBehavior.SetIsEnabled(this, true);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = CreateToolbar();
            Grid.SetRow(toolbar, 0);
            mainGrid.Children.Add(toolbar);

            // Row 1: DataGrid
            _viewSource = new CollectionViewSource { Source = _vm.Entries };
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
                Margin = new Thickness(0, 8, 0, 0)
            };
            _rerunButton.Command = _vm.RerunCommand;
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

            Content = mainGrid;
            _grid.SetBinding(DataGrid.SelectedItemProperty, new Binding(nameof(ActivityHistoryViewModel.SelectedEntry)) { Source = vm, Mode = BindingMode.TwoWay });
            _searchBox.SetBinding(TextBox.TextProperty, new Binding(nameof(ActivityHistoryViewModel.SearchText)) { Source = vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            _filterCombo.SelectedValuePath = _kindCombo.SelectedValuePath = "Tag";
            _filterCombo.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(ActivityHistoryViewModel.ResultFilter)) { Source = vm, Mode = BindingMode.TwoWay });
            _kindCombo.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(ActivityHistoryViewModel.KindFilter)) { Source = vm, Mode = BindingMode.TwoWay });
            _loadHistoryButton.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ActivityHistoryViewModel.LoadPastLabel)) { Source = vm });
            _rerunButton.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ActivityHistoryViewModel.RerunLabel)) { Source = vm });
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
        private void ApplyLocalization(bool restoreSelection = false)
        {
            var version = L.Version;
            var localeChanged = version != _lastSeenL10nVersion;
            _lastSeenL10nVersion = version;
            _activityGroup.Header = L.T("failure.tab.history");
            _detailGroup.Header = L.T("history.group.detail");
            _colTime.Header = L.T("history.col.time");
            _colTool.Header = L.T("history.col.tool");
            _colKind.Header = L.T("history.col.kind");
            _colSeconds.Header = L.T("history.col.seconds");
            _colStatus.Header = L.T("history.col.status");
            _searchPrompt.Text = L.T("history.search.placeholder");
            _logsButton.Content = L.T("history.toolbar.openLogs");
            _newSessionButton.Content = L.T("history.toolbar.newSession");
            foreach (ComboBoxItem item in _filterCombo.Items)
                item.Content = L.T(FilterKeys[(string)item.Tag]);
            foreach (ComboBoxItem item in _kindCombo.Items)
                item.Content = L.T(KindKeys[(string)item.Tag]);
            _whatSection.Text = L.T("history.section.what");
            _inputSection.Text = L.T("history.section.input");
            _outputSection.Text = L.T("history.section.output");
            // Regenerating 1000 summaries parses JSON + runs regex — only worth it
            // when the table actually changed, and PreserveSummary entries are skipped.
            if (localeChanged)
            {
                _viewSource.View.Refresh();
            }
            // Re-run results live in _outputContainer — a locale swap must not wipe them.
            if (restoreSelection || (!_rerunInProgress && !_detailShowsRerun))
                ShowEntry(_selectedEntry);
        }

        private DockPanel CreateToolbar()
        {
            var panel = new DockPanel { LastChildFill = false, Margin = new Thickness(8, 6, 8, 6) };
            var toolbarStyle = (Style)FindResource(BimwrightStyles.ToolbarButtonKey);
            var sessionActions = new StackPanel { Orientation = Orientation.Horizontal };

            _logsButton = new Button { Style = toolbarStyle };
            _logsButton.Command = _vm.OpenLogsCommand;
            sessionActions.Children.Add(_logsButton);

            _loadHistoryButton = new Button { Style = toolbarStyle };
            _loadHistoryButton.Command = _vm.LoadPastCommand;
            sessionActions.Children.Add(_loadHistoryButton);

            _newSessionButton = new Button { Style = toolbarStyle };
            _newSessionButton.Command = _vm.NewSessionCommand;
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
            filters.Children.Add(_kindCombo);

            _filterCombo = new ComboBox { MinWidth = 120, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var tag in new[] { "all", "success", "failed" })
                _filterCombo.Items.Add(new ComboBoxItem { Tag = tag });
            _filterCombo.SelectedIndex = 0;
            filters.Children.Add(_filterCombo);
            DockPanel.SetDock(filters, Dock.Left);
            panel.Children.Add(filters);
            return panel;
        }

        private void OnFilter(object sender, FilterEventArgs e) => e.Accepted = _vm.Matches(e.Item as McpCallEntry);

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
                _warningLabel.Text = SoftWrap(_vm.CanRerun(entry)
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

        private void RenderRerun()
        {
            _detailShowsRerun = true;
            var output = _vm.RerunOutput;
            if (output == null) return;
                // Show re-run result in output panel
                _outputContainer.Children.Clear();
                _outputContainer.Children.Add(new TextBlock
                {
                    Text = L.T("history.rerun.resultHeader", ("time", output.CompletedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture))),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4)
                });

                // Diff hints for numeric fields
                if (output.OriginalResultJson != null && output.Entry.ResultJson != null)
                {
                    try
                    {
                        var oldResult = JObject.Parse(output.OriginalResultJson);
                        var newResult = JObject.Parse(output.Entry.ResultJson);
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
                    Success = output.Entry.Success,
                    ResultJson = output.Entry.ResultJson,
                    ErrorMessage = output.Entry.ErrorMessage,
                    ToolName = output.Entry.ToolName
                };
                FormatOutput(rerunEntry, _outputContainer);

        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_disposed || _observing) return;
            _observing = true;
            _vm.PropertyChanged += OnViewModelChanged;
            _detailShowsRerun = _vm.RerunOutput != null || _vm.RerunError != null;
            // Selection can change through collection eviction while this renderer is detached.
            ApplyLocalization(restoreSelection: true);
            if (_vm.RerunOutput != null) RenderRerun();
            else if (_vm.RerunError != null) RenderRerunError();
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_observing) return;
            _observing = false;
            _vm.PropertyChanged -= OnViewModelChanged;
        }
        private void RenderRerunError()
        {
            _detailShowsRerun = true;
            _outputContainer.Children.Clear();
            _outputContainer.Children.Add(new TextBlock { Text = SoftWrap(_vm.RerunError), Foreground = Brushes.Red, TextWrapping = TextWrapping.Wrap });
        }
        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            if (e.PropertyName == nameof(ActivityHistoryViewModel.SelectedEntry)) ShowEntry(_vm.SelectedEntry);
            else if (e.PropertyName == nameof(ActivityHistoryViewModel.SearchText)
                || e.PropertyName == nameof(ActivityHistoryViewModel.ResultFilter) || e.PropertyName == nameof(ActivityHistoryViewModel.KindFilter)) _viewSource.View.Refresh();
            else if (e.PropertyName == nameof(ActivityHistoryViewModel.LanguageVersion)) ApplyLocalization();
            else if (e.PropertyName == nameof(ActivityHistoryViewModel.RerunOutput)) RenderRerun();
            else if (e.PropertyName == nameof(ActivityHistoryViewModel.RerunError))
            {
                RenderRerunError();
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            OnUnloaded(this, null);
            _viewSource.Filter -= OnFilter;
            _viewSource.Source = null;
            foreach (var control in new FrameworkElement[] { _grid, _searchBox, _filterCombo, _kindCombo, _loadHistoryButton, _rerunButton }) BindingOperations.ClearAllBindings(control);
            _rerunButton.Command = _loadHistoryButton.Command = _logsButton.Command = _newSessionButton.Command = null;
            DataContext = null;
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
