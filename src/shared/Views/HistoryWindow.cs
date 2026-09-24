using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
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
        private readonly StackPanel _detailPanel;
        private readonly TextBlock _whatName;
        private readonly TextBlock _whatDesc;
        private readonly TextBlock _warningLabel;
        private readonly ContentControl _inputContent;
        private readonly StackPanel _outputContainer;
        private readonly TextBlock _footerText;
        private readonly CommandDispatcher _dispatcher;
        private readonly McpEventHandler _eventHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent _externalEvent;
        private readonly Button _rerunButton;
        private Button _loadHistoryButton;
        private McpCallEntry _selectedEntry;
        private Dictionary<string, string> _journalBodyCache;

        public HistoryWindow(McpSessionLog sessionLog, CommandDispatcher dispatcher,
                             McpEventHandler eventHandler, Autodesk.Revit.UI.ExternalEvent externalEvent)
        {
            _sessionLog = sessionLog;
            _dispatcher = dispatcher;
            _eventHandler = eventHandler;
            _externalEvent = externalEvent;

            Title = "MCP Command History";
            Width = 900;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(250) });

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
            _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding("Index"), Width = 40 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding("TimeLabel"), Width = 80 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Tool", Binding = new Binding("ToolName"), Width = 160 });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Summary",
                Binding = new Binding("Summary"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                ElementStyle = CreateTrimStyle()
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Status",
                Binding = new Binding("Success") { Converter = new BoolToStatusConverter() },
                Width = 50
            });
            _grid.Columns.Add(new DataGridTextColumn { Header = "ms", Binding = new Binding("DurationMs"), Width = 55 });
            _grid.SelectionChanged += OnSelectionChanged;

            Grid.SetRow(_grid, 1);
            mainGrid.Children.Add(_grid);

            // Row 2: Detail panel
            var detailBorder = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = Brushes.LightGray,
                Margin = new Thickness(4)
            };
            var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _detailPanel = new StackPanel { Margin = new Thickness(4) };

            // WHAT section
            var whatHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var whatLabel = new TextBlock { Text = "WHAT", FontWeight = FontWeights.Bold, Foreground = Brushes.Gray, FontSize = 10, Width = 55 };
            _whatName = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 14 };
            _rerunButton = new Button
            {
                Content = "\u25B6 Re-run",
                Padding = new Thickness(12, 2, 12, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsEnabled = false
            };
            _rerunButton.Click += OnRerunClick;
            DockPanel.SetDock(_rerunButton, Dock.Right);
            whatHeader.Children.Add(_rerunButton);
            DockPanel.SetDock(whatLabel, Dock.Left);
            whatHeader.Children.Add(whatLabel);
            whatHeader.Children.Add(_whatName);
            _whatDesc = new TextBlock { FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(55, 0, 0, 2), TextWrapping = TextWrapping.Wrap };
            _warningLabel = new TextBlock { Foreground = Brushes.OrangeRed, FontSize = 11, Margin = new Thickness(55, 0, 0, 8), Visibility = Visibility.Collapsed };
            _detailPanel.Children.Add(whatHeader);
            _detailPanel.Children.Add(_whatDesc);
            _detailPanel.Children.Add(_warningLabel);

            // INPUT section
            var inputLabel = new TextBlock { Text = "INPUT", FontWeight = FontWeights.Bold, Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 4, 0, 2) };
            _inputContent = new ContentControl { Margin = new Thickness(55, 0, 0, 8) };
            _detailPanel.Children.Add(inputLabel);
            _detailPanel.Children.Add(_inputContent);

            // OUTPUT section
            var outputLabel = new TextBlock { Text = "OUTPUT", FontWeight = FontWeights.Bold, Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 4, 0, 2) };
            _outputContainer = new StackPanel { Margin = new Thickness(55, 0, 0, 8) };
            _detailPanel.Children.Add(outputLabel);
            _detailPanel.Children.Add(_outputContainer);

            // Footer
            _footerText = new TextBlock { Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 8, 0, 0) };
            _detailPanel.Children.Add(_footerText);

            detailScroll.Content = _detailPanel;
            detailBorder.Child = detailScroll;
            Grid.SetRow(detailBorder, 2);
            mainGrid.Children.Add(detailBorder);

            // Splitter between grid and detail
            var splitter = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brushes.Transparent
            };
            Grid.SetRow(splitter, 1);
            mainGrid.Children.Add(splitter);

            Content = mainGrid;
        }

        private StackPanel CreateToolbar()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4)
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Search:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            });

            _searchBox = new TextBox { Width = 150, Margin = new Thickness(0, 0, 8, 0) };
            _searchBox.TextChanged += (s, e) => _viewSource.View.Refresh();
            panel.Children.Add(_searchBox);

            panel.Children.Add(new TextBlock
            {
                Text = "Filter:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            });

            _filterCombo = new ComboBox { Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            _filterCombo.Items.Add("All");
            _filterCombo.Items.Add("Success");
            _filterCombo.Items.Add("Failed");
            _filterCombo.SelectedIndex = 0;
            _filterCombo.SelectionChanged += (s, e) => _viewSource.View.Refresh();
            panel.Children.Add(_filterCombo);

            panel.Children.Add(new TextBlock
            {
                Text = "Kind:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            });

            _kindCombo = new ComboBox { Width = 70, Margin = new Thickness(0, 0, 8, 0) };
            _kindCombo.Items.Add("All");
            _kindCombo.Items.Add("Read");
            _kindCombo.Items.Add("Write");
            _kindCombo.SelectedIndex = 0;
            _kindCombo.SelectionChanged += (s, e) => _viewSource.View.Refresh();
            panel.Children.Add(_kindCombo);

            var logsBtn = new Button { Content = "Open logs", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 8, 0) };
            logsBtn.Click += (s, e) => OpenLogFolder();
            panel.Children.Add(logsBtn);

            _loadHistoryButton = new Button { Content = "Load past sessions", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 8, 0) };
            _loadHistoryButton.Click += (s, e) => LoadPastSessions();
            panel.Children.Add(_loadHistoryButton);

            var clearBtn = new Button { Content = "Clear Session", Padding = new Thickness(8, 2, 8, 2) };
            clearBtn.Click += (s, e) =>
            {
                _sessionLog.Clear();
                _whatName.Text = "";
                _whatDesc.Text = "Select a command to view details";
                _warningLabel.Visibility = Visibility.Collapsed;
                _inputContent.Content = null;
                _outputContainer.Children.Clear();
                _footerText.Text = "";
            };
            panel.Children.Add(clearBtn);

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

            var filter = _filterCombo?.SelectedItem as string;
            if (filter == "Success" && !entry.Success) { e.Accepted = false; return; }
            if (filter == "Failed" && entry.Success) { e.Accepted = false; return; }

            var kind = _kindCombo?.SelectedItem as string;
            if (kind == "Read" && ToolActivityClassifier.Classify(entry.ToolName) != ToolActivityKind.Read) { e.Accepted = false; return; }
            if (kind == "Write" && ToolActivityClassifier.Classify(entry.ToolName) != ToolActivityKind.Write) { e.Accepted = false; return; }

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

            var entries = SessionLogHistoryLoader.LoadPastSessions(logDir, McpLogger.CurrentSessionId);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entry.ToolDescription = _dispatcher.GetCommand(entry.ToolName)?.Description;
                _sessionLog.Entries.Insert(i, entry);
            }

            _loadHistoryButton.IsEnabled = false;
            _loadHistoryButton.Content = entries.Count == 0 ? "No past entries" : $"Loaded {entries.Count} past";
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

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Guard: event fires during construction before detail panel fields exist
            if (_whatName == null) return;

            var entry = _grid.SelectedItem as McpCallEntry;
            _selectedEntry = entry;
            _rerunButton.IsEnabled = IsRerunPossible(entry);

            if (entry == null)
            {
                _whatName.Text = "";
                _whatDesc.Text = "Select a command to view details";
                _warningLabel.Visibility = Visibility.Collapsed;
                _inputContent.Content = null;
                _outputContainer.Children.Clear();
                _footerText.Text = "";
                return;
            }

            // WHAT
            _whatName.Text = entry.ToolName;
            _whatDesc.Text = entry.ToolDescription ?? "";
            _warningLabel.Foreground = Brushes.OrangeRed;
            if (entry.IsHistorical)
            {
                _warningLabel.Text = "From a previous session — view only";
                _warningLabel.Foreground = Brushes.Gray;
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ToolName == "send_code_to_revit" && string.IsNullOrEmpty(entry.CodeSnippet))
            {
                _warningLabel.Text = IsRerunPossible(entry)
                    ? "⚠ send_code body recovered from journal is bake-redacted (paths/secrets → placeholders) — verify before re-run"
                    : "⚠ send_code body redacted from history (code_hash only) — re-run unavailable";
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ParamsTruncated)
            {
                _warningLabel.Text = "⚠ Params truncated in history (large payload) — re-run unavailable";
                _warningLabel.Visibility = Visibility.Visible;
            }
            else if (entry.ToolName == "send_code_to_revit")
            {
                _warningLabel.Text = "\u26A0 Compile + execute C# inside Revit (dangerous)";
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
            var rerunNote = entry.RerunOfIndex.HasValue ? $" \u00B7 re-run of #{entry.RerunOfIndex}" : "";
            var sessionNote = entry.IsHistorical ? $" \u00B7 session {entry.SessionTag}" : "";
            _footerText.Text = $"{entry.DurationMs}ms \u00B7 {(entry.Success ? "OK" : "FAIL")} \u00B7 {entry.TimeLabel}{rerunNote}{sessionNote}";
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
                        Header = $"Code ({lines.Length} lines)",
                        IsExpanded = false,
                        Content = codeBox
                    };
                }
                return codeBox;
            }

            if (string.IsNullOrEmpty(entry.ParamsJson))
                return new TextBlock { Text = "(no parameters)", Foreground = Brushes.Gray };

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
                    Text = entry.ErrorMessage ?? "Unknown error",
                    Foreground = Brushes.Red,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            if (string.IsNullOrEmpty(entry.ResultJson))
            {
                container.Children.Add(new TextBlock { Text = "(no output)", Foreground = Brushes.Gray });
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
                            Text = $"Showing 10 of {rows.Count} rows",
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
                    ? "Body recovered from the send-code journal is redacted (paths/secrets → placeholders) — it may behave differently than the original. Execute anyway?"
                    : "This will execute C# code in Revit. Continue?";
                var confirm = MessageBox.Show(
                    this,
                    prompt,
                    "Re-run Confirmation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            _rerunButton.Content = "Running...";
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
                    Text = $"Re-run result ({DateTime.Now:HH:mm:ss})",
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
                                        Text = $"  {prop.Name}: {newVal} (was {oldVal} \u2014 {sign}{delta})",
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

                _footerText.Text = $"{sw.ElapsedMilliseconds}ms \u00B7 {(result.Success ? "OK" : "FAIL")} \u00B7 re-run of #{originalIndex}";
            }
            catch (Exception ex)
            {
                _outputContainer.Children.Clear();
                _outputContainer.Children.Add(new TextBlock
                {
                    Text = $"Re-run failed: {ex.Message}",
                    Foreground = Brushes.Red,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            finally
            {
                _rerunButton.Content = "\u25B6 Re-run";
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

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }

    internal class BoolToStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is bool b && b ? "OK" : "FAIL";

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}
