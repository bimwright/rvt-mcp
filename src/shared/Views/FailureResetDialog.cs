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
            Resources.MergedDictionaries.Add(BimwrightStyles.Dictionary);
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            _acknowledged = new CheckBox {
                Content = new TextBlock { Text = L.T("failure.reset.agents"), TextWrapping = TextWrapping.Wrap },
                IsChecked = true,
                Margin = new Thickness(0, 16, 0, 16)
            };
            panel.Children.Add(_acknowledged);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = L.T("security.failure.review.cancel"), IsCancel = true, MinWidth = 110,
                Margin = new Thickness(0, 0, 8, 0) };
            _reset = new Button { Content = L.T("failure.reset"), MinWidth = 110,
                Style = (Style)BimwrightStyles.Dictionary[BimwrightStyles.PrimaryButtonKey] };
            _acknowledged.Checked += (s, e) => _reset.IsEnabled = true;
            _acknowledged.Unchecked += (s, e) => _reset.IsEnabled = false;
            _reset.Click += (s, e) => { if (_acknowledged.IsChecked == true) DialogResult = true; };
            buttons.Children.Add(cancel);
            buttons.Children.Add(_reset);
            panel.Children.Add(buttons);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += (s, e) => cancel.Focus();
        }
    }
}
