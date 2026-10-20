using System.Windows;
using System.Windows.Controls;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views
{
    // The user's acknowledgment covers agent work outside MCP; the store separately guards running MCP scopes.
    internal sealed class FailureResetDialog : Window
    {
        private readonly CheckBox _acknowledged;
        private readonly Button _reset;

        internal FailureResetDialog(string message)
        {
            Title = L.T("failure.reset.title");
            Width = 560; SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height * 0.9;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            _acknowledged = new CheckBox {
                Content = new TextBlock { Text = L.T("failure.reset.agents"), TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(0, 16, 0, 16)
            };
            panel.Children.Add(_acknowledged);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _reset = new Button { Content = L.T("failure.reset"), IsEnabled = false, MinWidth = 90, Padding = new Thickness(12, 6, 12, 6) };
            _acknowledged.Checked += (s, e) => _reset.IsEnabled = true;
            _acknowledged.Unchecked += (s, e) => _reset.IsEnabled = false;
            _reset.Click += (s, e) => { if (_acknowledged.IsChecked == true) DialogResult = true; };
            buttons.Children.Add(_reset);
            buttons.Children.Add(new Button { Content = L.T("security.failure.review.cancel"), IsCancel = true, MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) });
            panel.Children.Add(buttons);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }
}
