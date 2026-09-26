using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RvtMcp.Plugin.Views.Settings
{
    public sealed class LicenseWindow : Window
    {
        public LicenseWindow(AboutInfo about, Window owner = null)
        {
            Title = SettingsText.Text("settings.license.title", "rvt-mcp · License");
            // Opens at its minimum width; the license text wraps, so it only needs 10 % more room.
            MinWidth = 520;
            Width = MinWidth;
            MaxWidth = MinWidth * 1.1;
            Height = 620;
            MinHeight = 420;
            MaxHeight = Height * 1.1;
            Owner = owner;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            Foreground = SettingsStyles.Text;
            Background = SettingsStyles.Background;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Resources.MergedDictionaries.Add(SettingsStyles.Create());
            ScrollBarFadeBehavior.SetIsEnabled(this, true);

            var body = new DockPanel { Margin = new Thickness(16, 14, 16, 14) };
            var heading = new TextBlock { Text = SettingsText.Text("settings.license.summary", "rvt-mcp is distributed under the Apache License 2.0."), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(heading, Dock.Top);
            body.Children.Add(heading);
            var disclaimer = new TextBlock { Text = SettingsText.Text("settings.license.disclaimer", "This explanation does not replace the official license text."), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = SettingsStyles.Secondary, Margin = new Thickness(0, 2, 0, 12) };
            DockPanel.SetDock(disclaimer, Dock.Top);
            body.Children.Add(disclaimer);
            var text = LicenseProvider.Load(Assembly.GetExecutingAssembly());
            if (text == null)
            {
                body.Children.Add(new TextBlock { Text = SettingsText.Text("settings.license.loadError", "The embedded license text could not be loaded. See the diagnostic log."), TextWrapping = TextWrapping.Wrap, Foreground = SettingsStyles.Error });
            }
            else
            {
                body.Children.Add(new Border
                {
                    Background = SettingsStyles.Surface,
                    BorderBrush = SettingsStyles.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(1),
                    Child = new TextBox
                    {
                        Text = text,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        BorderThickness = new Thickness(0),
                        Background = Brushes.Transparent,
                        Foreground = SettingsStyles.Text,
                        FontFamily = new FontFamily("Consolas"),
                        // 77-column LICENSE lines fit the minimum width without re-wrapping.
                        FontSize = 10.5,
                        Padding = new Thickness(8, 8, 2, 8),
                    },
                });
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var open = new Button { Content = SettingsText.Text("settings.license.openGithub", "Open on GitHub ↗"), Margin = new Thickness(0, 0, 8, 0) };
            open.Click += (_, __) => OpenUrl(about?.LicenseSourceUrl);
            var close = new Button { Content = SettingsText.Text("settings.license.close", "Close"), Style = (Style)FindResource("Primary"), MinWidth = 88, IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(open);
            buttons.Children.Add(close);
            var footer = new Border
            {
                Background = SettingsStyles.Surface,
                BorderBrush = SettingsStyles.Line,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 10, 16, 10),
                Child = buttons,
            };

            var root = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(body);
            Content = root;
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
