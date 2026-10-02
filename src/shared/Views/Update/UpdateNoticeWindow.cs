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
    /// the work area. Never modal, never steals focus; closes on any button.
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
            Width = 440;
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

            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };
            body.Children.Add(new TextBlock
            {
                Text = T("update.message",
                    "A new version of rvt-mcp is available ({latest}). Copy the prompt below and paste it to your AI agent to update.",
                    ("latest", info.LatestVersion)),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
            });
            body.Children.Add(new TextBlock
            {
                Text = T("update.installed", "Installed: {current}", ("current", info.CurrentVersion)),
                Foreground = SettingsStyles.Secondary,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 10),
            });
            body.Children.Add(new Border
            {
                Background = SettingsStyles.Surface,
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBox
                {
                    Text = _prompt,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 150,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = SettingsStyles.Text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Padding = new Thickness(6),
                },
            });

            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _copy = new Button { Content = T("update.copyPrompt", "Copy prompt"), Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 0, 8, 0), MinWidth = 96 };
            _copy.Click += (_, __) => CopyPrompt();
            var open = new Button { Content = T("update.openRelease", "Open release page"), Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (_, __) => OpenUrl(_info.ReleaseUrl);
            var skip = new Button { Content = T("update.skip", "Skip this version"), Margin = new Thickness(0, 0, 8, 0) };
            skip.Click += (_, __) => { UpdateChecker.SkipVersion(_info.LatestVersion); Close(); };
            var close = new Button { Content = T("update.close", "Close"), IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(_copy);
            buttons.Children.Add(open);
            buttons.Children.Add(skip);
            buttons.Children.Add(close);
            body.Children.Add(buttons);
            Content = body;

            Loaded += (_, __) => PlaceBottomRight();
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

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
