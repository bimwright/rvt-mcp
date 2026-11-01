using System.Windows;
using System.Windows.Controls;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Reports.Views
{
    /// <summary>History's eased wheel scrolling and white edge fades, applied to a Reports scroll region.</summary>
    internal static class ReportsScrolling
    {
        /// <summary>
        /// Wraps <paramref name="scrolling"/> (a ScrollViewer, or a control whose template has one) in a grid
        /// with top and bottom fades; each fade shows only while content is hidden on its side.
        /// </summary>
        internal static Grid Smooth(Control scrolling, double step)
        {
            var top = HistoryChrome.FadeEdge(true);
            var bottom = HistoryChrome.FadeEdge(false);
            ScrollViewer Viewer() => scrolling as ScrollViewer ?? HistoryChrome.FindScrollViewer(scrolling);
            void Update()
            {
                var viewer = Viewer();
                var canScroll = scrolling.IsVisible && viewer != null && viewer.ScrollableHeight > 0;
                HistoryChrome.FadeTo(top, canScroll && viewer.VerticalOffset > 1);
                HistoryChrome.FadeTo(bottom, canScroll && viewer.VerticalOffset < viewer.ScrollableHeight - 1);
            }
            scrolling.PreviewMouseWheel += (s, e) => HistoryChrome.Wheel(Viewer(), e, step);
            // ScrollChanged also reports extent and viewport changes, so new content or a resize updates the fades.
            scrolling.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) => Update()));
            scrolling.IsVisibleChanged += (s, e) => Update();
            var host = new Grid { Tag = "SmoothScroll" };
            host.Children.Add(scrolling);
            host.Children.Add(top);
            host.Children.Add(bottom);
            return host;
        }
    }
}
