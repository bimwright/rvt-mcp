using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RvtMcp.Plugin.Update;
using RvtMcp.Plugin.Views.Settings;

namespace RvtMcp.Plugin.Views.Update
{
    /// <summary>
    /// Small modeless "update available" card shown in the bottom-right corner of
    /// the work area. Never modal, never steals focus.
    /// </summary>
    public sealed class UpdateNoticeWindow : Window
    {
        private readonly UpdateInfo _info;
        private readonly string _prompt;
        private readonly Button _copy;

        public UpdateNoticeWindow(UpdateInfo info)
        {
            _info = info ?? throw new ArgumentNullException(nameof(info));
            _prompt = UpdateNoticeText.BuildAgentPrompt(info);

            Title = T("update.title", "rvt-mcp update available");
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.ToolWindow;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            Foreground = SettingsStyles.Text;
            Background = SettingsStyles.Background;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Resources.MergedDictionaries.Add(SettingsStyles.Create());
            ScrollBarFadeBehavior.SetIsEnabled(this, true);

            var root = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };

            // Header: version badge on the left, subtle release-page link on the right.
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pillText = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pillText.Children.Add(new TextBlock
            {
                Text = info.CurrentVersion,
                Foreground = SettingsStyles.Secondary,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            pillText.Children.Add(new TextBlock
            {
                Text = " → ",
                Foreground = SettingsStyles.Secondary,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
            pillText.Children.Add(new TextBlock
            {
                Text = info.LatestVersion,
                Foreground = SettingsStyles.Blue,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var versionPill = new Border
            {
                Background = SettingsStyles.NeutralFill,
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = pillText,
            };
            Grid.SetColumn(versionPill, 0);
            header.Children.Add(versionPill);

            var openLink = new Button
            {
                Content = T("update.openRelease", "Open release page") + " ↗",
                Style = (Style)FindResource("Link"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            openLink.Click += (_, __) => OpenUrl(_info.ReleaseUrl);
            Grid.SetColumn(openLink, 1);
            header.Children.Add(openLink);
            root.Children.Add(header);

            root.Children.Add(new TextBlock
            {
                Text = T("update.message",
                    "A new version of rvt-mcp is available ({latest}). Copy the prompt below and paste it to your AI agent to update.",
                    ("latest", info.LatestVersion)),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            // Changelog / What's new (shown when GitHub release notes were captured).
            if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
            {
                root.Children.Add(new TextBlock
                {
                    Text = T("update.whatsNew", "What's new"),
                    Foreground = SettingsStyles.Secondary,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4),
                });
                root.Children.Add(new Border
                {
                    Background = SettingsStyles.Surface,
                    BorderBrush = SettingsStyles.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 0, 10),
                    Child = new TextBox
                    {
                        Text = info.ReleaseNotes,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        MaxHeight = 160,
                        BorderThickness = new Thickness(0),
                        Background = Brushes.Transparent,
                        Foreground = SettingsStyles.Text,
                        FontSize = 12,
                        Padding = new Thickness(10, 8, 10, 8),
                    },
                });
            }

            // Collapsible agent prompt preview (collapsed by default when release notes exist).
            var promptExpanded = string.IsNullOrWhiteSpace(info.ReleaseNotes);
            var promptLabel = T("update.previewPrompt", "Preview agent update prompt");
            var promptBox = new Border
            {
                Background = SettingsStyles.Surface,
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 4, 0, 12),
                Visibility = promptExpanded ? Visibility.Visible : Visibility.Collapsed,
                Child = new TextBox
                {
                    Text = _prompt,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 130,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = SettingsStyles.Text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Padding = new Thickness(8, 6, 8, 6),
                },
            };
            var promptToggle = new Button
            {
                Content = (promptExpanded ? "▾ " : "▸ ") + promptLabel,
                Style = (Style)FindResource("Link"),
                Foreground = SettingsStyles.Secondary,
                FontSize = 12,
                FontWeight = FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, promptExpanded ? 0 : 8),
            };
            promptToggle.Click += (_, __) =>
            {
                promptExpanded = !promptExpanded;
                promptToggle.Content = (promptExpanded ? "▾ " : "▸ ") + promptLabel;
                promptToggle.Margin = new Thickness(0, 0, 0, promptExpanded ? 0 : 8);
                promptBox.Visibility = promptExpanded ? Visibility.Visible : Visibility.Collapsed;
            };
            root.Children.Add(promptToggle);
            root.Children.Add(promptBox);

            // Footer: single guaranteed row — Skip link on left, primary Copy + Close on right.
            var footerDivider = new Border
            {
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 10, 0, 0),
            };
            var footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var skip = new Button
            {
                Content = T("update.skip", "Skip this version"),
                Style = (Style)FindResource("Link"),
                Foreground = SettingsStyles.Secondary,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            skip.Click += (_, __) => { UpdateChecker.SkipVersion(_info.LatestVersion); Close(); };
            Grid.SetColumn(skip, 0);
            footer.Children.Add(skip);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _copy = new Button
            {
                Content = T("update.copyPrompt", "Copy prompt"),
                Style = (Style)FindResource("Primary"),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 104,
            };
            _copy.Click += (_, __) => CopyPrompt();
            var close = new Button
            {
                Content = T("update.close", "Close"),
                IsCancel = true,
                MinWidth = 72,
            };
            close.Click += (_, __) => Close();
            actions.Children.Add(_copy);
            actions.Children.Add(close);
            Grid.SetColumn(actions, 1);
            footer.Children.Add(actions);

            footerDivider.Child = footer;
            root.Children.Add(footerDivider);
            Content = root;

            Loaded += (_, __) => PlaceBottomRight();
            SizeChanged += (_, __) => ClampInsideWorkArea();
        }

        // SettingsText falls back to the raw English text without filling placeholders.
        private static string T(string key, string fallback, params (string Name, object Value)[] args)
        {
            var text = SettingsText.Text(key, fallback, args);
            foreach (var (name, value) in args)
                text = text.Replace("{" + name + "}", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            return text;
        }

        private void CopyPrompt()
        {
            try
            {
                Clipboard.SetText(_prompt);
                _copy.Content = T("update.copied", "Copied ✓");
            }
            catch { }
        }

        private void PlaceBottomRight()
        {
            try
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth - 16;
                Top = area.Bottom - ActualHeight - 16;
            }
            catch { }
        }

        private void ClampInsideWorkArea()
        {
            if (!IsLoaded) return;
            try
            {
                var area = SystemParameters.WorkArea;
                if (Top + ActualHeight > area.Bottom - 16)
                    Top = Math.Max(area.Top + 16, area.Bottom - ActualHeight - 16);
            }
            catch { }
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
