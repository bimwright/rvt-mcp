using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RvtMcp.Plugin.Views
{
    /// <summary>Shared History window chrome: flat tabs, wrapped tips, and eased scrolling.</summary>
    internal static class HistoryChrome
    {
        internal static Grid WithWatermark(TextBox input, TextBlock watermark)
        {
            watermark.IsHitTestVisible = false;
            void Refresh() => watermark.Visibility = string.IsNullOrEmpty(input.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            input.TextChanged += (s, e) => Refresh();
            Refresh();
            var host = new Grid();
            host.Children.Add(input);
            host.Children.Add(watermark);
            return host;
        }

        private static readonly DependencyProperty SmoothOffsetProperty = DependencyProperty.RegisterAttached(
            "SmoothOffset", typeof(double), typeof(HistoryChrome),
            new PropertyMetadata(0d, (d, e) => (d as ScrollViewer)?.ScrollToVerticalOffset((double)e.NewValue)));

        private static readonly DependencyProperty SmoothStateProperty = DependencyProperty.RegisterAttached(
            "SmoothState", typeof(WheelState), typeof(HistoryChrome), new PropertyMetadata(null));

        internal static ResourceDictionary TabDictionary()
        {
            const string xaml = @"<ResourceDictionary
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Style TargetType=""{x:Type TabControl}"">
        <Setter Property=""Background"" Value=""White""/>
        <Setter Property=""BorderThickness"" Value=""0""/>
        <Setter Property=""Padding"" Value=""0""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type TabControl}"">
                    <DockPanel SnapsToDevicePixels=""True"" UseLayoutRounding=""True"">
                        <Border DockPanel.Dock=""Top"" Background=""#FFF7F8FA"" BorderBrush=""#FFE0E0E0"" BorderThickness=""0,0,0,1"" SnapsToDevicePixels=""True"">
                            <TabPanel IsItemsHost=""True"" Margin=""16,8,16,0"" Background=""Transparent""/>
                        </Border>
                        <ContentPresenter ContentSource=""SelectedContent""/>
                    </DockPanel>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType=""{x:Type TabItem}"">
        <Setter Property=""FontFamily"" Value=""Segoe UI""/>
        <Setter Property=""FontSize"" Value=""13""/>
        <Setter Property=""Foreground"" Value=""#FF64748B""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Padding"" Value=""2,8""/>
        <Setter Property=""Margin"" Value=""0""/>
        <Setter Property=""FocusVisualStyle"" Value=""{x:Null}""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type TabItem}"">
                    <Border Margin=""0,0,22,0"" Background=""Transparent"">
                        <Border x:Name=""Head"" Background=""{TemplateBinding Background}""
                                Padding=""{TemplateBinding Padding}""
                                Margin=""0,0,0,-1""
                                BorderThickness=""0,0,0,2"" BorderBrush=""Transparent"">
                            <ContentPresenter ContentSource=""Header"" RecognizesAccessKey=""True"" HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                        </Border>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter Property=""Foreground"" Value=""#FF1E293B""/>
                        </Trigger>
                        <Trigger Property=""IsSelected"" Value=""True"">
                            <Setter Property=""Foreground"" Value=""#FF007ACC""/>
                            <Setter Property=""FontWeight"" Value=""SemiBold""/>
                            <Setter TargetName=""Head"" Property=""BorderBrush"" Value=""#FF007ACC""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>";
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        internal static ToolTip Tip(string text)
        {
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
            var source = text ?? "";
            var index = 0;
            while (index < source.Length)
            {
                var open = source.IndexOf("**", index, StringComparison.Ordinal);
                if (open < 0)
                {
                    block.Inlines.Add(new Run(source.Substring(index)));
                    break;
                }
                if (open > index) block.Inlines.Add(new Run(source.Substring(index, open - index)));
                var close = source.IndexOf("**", open + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    block.Inlines.Add(new Run(source.Substring(open)));
                    break;
                }
                block.Inlines.Add(new Run(source.Substring(open + 2, close - open - 2)) { FontWeight = FontWeights.Bold });
                index = close + 2;
            }
            return new ToolTip { Content = block };
        }

        internal static void Guide(FrameworkElement element, string text)
        {
            if (element == null) return;
            element.ToolTip = Tip(text);
            ToolTipService.SetShowOnDisabled(element, true);
        }

        internal static void Wheel(ScrollViewer viewer, MouseWheelEventArgs e, double step)
        {
            if (viewer == null || e == null || viewer.ScrollableHeight <= 0) return;
            e.Handled = true;
            var state = viewer.GetValue(SmoothStateProperty) as WheelState;
            if (state == null)
            {
                state = new WheelState();
                viewer.SetValue(SmoothStateProperty, state);
            }
            var current = viewer.VerticalOffset;
            if (!state.Wheeling) state.Target = current;
            state.Target = Math.Max(0, Math.Min(viewer.ScrollableHeight, state.Target - e.Delta * step));
            var distance = Math.Abs(state.Target - current);
            if (distance < 0.5) return;
            var token = ++state.Token;
            state.Wheeling = true;
            viewer.BeginAnimation(SmoothOffsetProperty, null);
            viewer.SetCurrentValue(SmoothOffsetProperty, current);
            var duration = step < 0.4
                ? Math.Min(460, 220 + distance * 0.55)
                : Math.Min(320, 140 + distance * 0.35);
            var animation = new DoubleAnimation(current, state.Target, TimeSpan.FromMilliseconds(duration))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            animation.Completed += (s, args) =>
            {
                if (token == state.Token) state.Wheeling = false;
            };
            viewer.BeginAnimation(SmoothOffsetProperty, animation);
        }

        internal static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var viewer = child as ScrollViewer ?? FindScrollViewer(child);
                if (viewer != null) return viewer;
            }
            return null;
        }

        internal static Border FadeEdge(bool top)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(top ? (byte)96 : (byte)0, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(top ? (byte)0 : (byte)96, 255, 255, 255), 1));
            brush.Freeze();
            return new Border
            {
                Height = 14,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Opacity = 0,
                Background = brush
            };
        }

        internal static void FadeTo(UIElement fade, bool show)
        {
            if (fade == null) return;
            fade.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private sealed class WheelState
        {
            public double Target;
            public bool Wheeling;
            public int Token;
        }
    }
}
