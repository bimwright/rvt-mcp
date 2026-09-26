using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace RvtMcp.Plugin.Views.Settings
{
    public sealed class LicenseWindow : Window
    {
        public LicenseWindow(AboutInfo about, Window owner = null)
        {
            Title = SettingsText.Text("settings.license.title", "rvt-mcp · License");
            Width = 720;
            Height = 620;
            MinWidth = 520;
            MinHeight = 420;
            Owner = owner;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            var root = new DockPanel { Margin = new Thickness(16) };
            var heading = new TextBlock { Text = SettingsText.Text("settings.license.summary", "rvt-mcp is distributed under the Apache License 2.0."), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var disclaimer = new TextBlock { Text = SettingsText.Text("settings.license.disclaimer", "This explanation does not replace the official license text."), TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 8, 0, 10) };
            DockPanel.SetDock(disclaimer, Dock.Top);
            root.Children.Add(disclaimer);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(footer, Dock.Bottom);
            var open = new Button { Content = SettingsText.Text("settings.license.openGithub", "Open on GitHub ↗"), Margin = new Thickness(0, 10, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
            open.Click += (_, __) => OpenUrl(about?.LicenseSourceUrl);
            var close = new Button { Content = SettingsText.Text("settings.license.close", "Close"), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            close.Click += (_, __) => Close();
            footer.Children.Add(open); footer.Children.Add(close); root.Children.Add(footer);
            var text = LicenseProvider.Load(Assembly.GetExecutingAssembly());
            if (text == null)
            {
                root.Children.Add(new TextBlock { Text = SettingsText.Text("settings.license.loadError", "The embedded license text could not be loaded. See the diagnostic log."), TextWrapping = TextWrapping.Wrap });
            }
            else
            {
                root.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
            Content = root;
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
