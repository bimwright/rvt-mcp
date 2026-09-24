using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RvtMcp.Plugin.Views
{
    /// <summary>
    /// Overlay scrollbar behavior: hidden by default, revealed on scroll activity or pointer
    /// proximity to the scrollbar track (not on generic host hover).
    /// </summary>
    public static class ScrollBarFadeBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(ScrollBarFadeBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static readonly DependencyProperty CollapsedOpacityProperty =
            DependencyProperty.RegisterAttached(
                "CollapsedOpacity",
                typeof(double),
                typeof(ScrollBarFadeBehavior),
                new PropertyMetadata(0.22));

        public static readonly DependencyProperty ExpandedOpacityProperty =
            DependencyProperty.RegisterAttached(
                "ExpandedOpacity",
                typeof(double),
                typeof(ScrollBarFadeBehavior),
                new PropertyMetadata(1.0));

        public static readonly DependencyProperty FadeMillisecondsProperty =
            DependencyProperty.RegisterAttached(
                "FadeMilliseconds",
                typeof(int),
                typeof(ScrollBarFadeBehavior),
                new PropertyMetadata(160));

        public static readonly DependencyProperty HideDelayMillisecondsProperty =
            DependencyProperty.RegisterAttached(
                "HideDelayMilliseconds",
                typeof(int),
                typeof(ScrollBarFadeBehavior),
                new PropertyMetadata(1200));

        public static readonly DependencyProperty ProximityZonePixelsProperty =
            DependencyProperty.RegisterAttached(
                "ProximityZonePixels",
                typeof(int),
                typeof(ScrollBarFadeBehavior),
                new PropertyMetadata(28));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(BehaviorState),
                typeof(ScrollBarFadeBehavior),
                new PropertyMetadata(null));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        public static double GetCollapsedOpacity(DependencyObject obj) => (double)obj.GetValue(CollapsedOpacityProperty);
        public static void SetCollapsedOpacity(DependencyObject obj, double value) => obj.SetValue(CollapsedOpacityProperty, value);

        public static double GetExpandedOpacity(DependencyObject obj) => (double)obj.GetValue(ExpandedOpacityProperty);
        public static void SetExpandedOpacity(DependencyObject obj, double value) => obj.SetValue(ExpandedOpacityProperty, value);

        public static int GetFadeMilliseconds(DependencyObject obj) => (int)obj.GetValue(FadeMillisecondsProperty);
        public static void SetFadeMilliseconds(DependencyObject obj, int value) => obj.SetValue(FadeMillisecondsProperty, value);

        public static int GetHideDelayMilliseconds(DependencyObject obj) => (int)obj.GetValue(HideDelayMillisecondsProperty);
        public static void SetHideDelayMilliseconds(DependencyObject obj, int value) => obj.SetValue(HideDelayMillisecondsProperty, value);

        public static int GetProximityZonePixels(DependencyObject obj) => (int)obj.GetValue(ProximityZonePixelsProperty);
        public static void SetProximityZonePixels(DependencyObject obj, int value) => obj.SetValue(ProximityZonePixelsProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement host)) return;

            Detach(host);

            if ((bool)e.NewValue)
            {
                var state = new BehaviorState(host);
                host.SetValue(StateProperty, state);

                if (host.IsLoaded)
                {
                    state.Attach();
                    state.OnHostReady();
                }
                else
                {
                    host.Loaded += OnHostLoaded;
                }
            }
            else
            {
                ResetScrollBars(host);
                host.ClearValue(StateProperty);
            }
        }

        private static void OnHostLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement host)) return;
            host.Loaded -= OnHostLoaded;
            BehaviorState state = GetState(host);
            if (state == null || !GetIsEnabled(host)) return;

            state.Attach();
            state.OnHostReady();
        }

        private static void Detach(FrameworkElement host)
        {
            host.Loaded -= OnHostLoaded;
            GetState(host)?.Detach();
        }

        private static BehaviorState GetState(FrameworkElement host) => (BehaviorState)host.GetValue(StateProperty);

        private static void FadeScrollBars(FrameworkElement host, bool expanded)
        {
            double targetOpacity = expanded
                ? CoerceOpacity(GetExpandedOpacity(host))
                : CoerceOpacity(GetCollapsedOpacity(host));
            int fadeMilliseconds = Math.Max(0, GetFadeMilliseconds(host));

            foreach (var scrollBar in FindVisualChildren<ScrollBar>(host))
            {
                if (fadeMilliseconds == 0)
                {
                    scrollBar.BeginAnimation(UIElement.OpacityProperty, null);
                    scrollBar.Opacity = targetOpacity;
                    continue;
                }

                var animation = new DoubleAnimation
                {
                    To = targetOpacity,
                    Duration = TimeSpan.FromMilliseconds(fadeMilliseconds),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                scrollBar.BeginAnimation(
                    UIElement.OpacityProperty,
                    animation,
                    HandoffBehavior.SnapshotAndReplace);
            }
        }

        private static void ResetScrollBars(FrameworkElement host)
        {
            foreach (var scrollBar in FindVisualChildren<ScrollBar>(host))
            {
                scrollBar.BeginAnimation(UIElement.OpacityProperty, null);
                scrollBar.ClearValue(UIElement.OpacityProperty);
            }
        }

        private static double CoerceOpacity(double value)
        {
            if (double.IsNaN(value)) return 1.0;
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    yield return match;

                foreach (T nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }

        private static bool IsScrollKey(Key key)
        {
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.PageUp:
                case Key.PageDown:
                case Key.Home:
                case Key.End:
                    return true;
                default:
                    return false;
            }
        }

        private sealed class BehaviorState
        {
            private readonly FrameworkElement _host;
            private readonly DispatcherTimer _hideTimer;
            private readonly HashSet<ScrollBar> _hookedScrollBars = new HashSet<ScrollBar>();
            private readonly List<ScrollBarHitBounds> _scrollBarHitBounds = new List<ScrollBarHitBounds>();
            private ScrollViewer _scrollViewer;
            private Window _window;
            private bool _isExpanded;
            private bool _isPointerOverScrollbar;
            private bool _isInProximityZone;
            private bool _layoutRefreshQueued;

            public BehaviorState(FrameworkElement host)
            {
                _host = host;
                _hideTimer = new DispatcherTimer();
                _hideTimer.Tick += OnHideTimerTick;
            }

            public void Attach()
            {
                _host.Unloaded += OnHostUnloaded;
                _host.LayoutUpdated += OnLayoutUpdated;
                _host.PreviewMouseWheel += OnScrollActivity;
                _host.PreviewKeyDown += OnPreviewKeyDown;

                if (_host is ScrollViewer scrollViewer)
                    _scrollViewer = scrollViewer;
            }

            public void Detach()
            {
                CancelHide();
                _host.Unloaded -= OnHostUnloaded;
                _host.LayoutUpdated -= OnLayoutUpdated;
                _host.PreviewMouseWheel -= OnScrollActivity;
                _host.PreviewKeyDown -= OnPreviewKeyDown;
                DetachScrollViewer();
                DetachScrollBarHooks();
                DetachWindowHandlers();
                _scrollBarHitBounds.Clear();
                _isPointerOverScrollbar = false;
                _isInProximityZone = false;
                _isExpanded = false;
            }

            public void OnHostReady()
            {
                AttachWindowHandlers();
                EnsureScrollViewerHook();
                RefreshScrollBarHooks();
                Collapse(immediate: true);
            }

            private void OnHostUnloaded(object sender, RoutedEventArgs e)
            {
                Detach();
                if (GetIsEnabled(_host))
                    _host.Loaded += OnHostLoaded;
            }

            private void OnLayoutUpdated(object sender, EventArgs e)
            {
                if (_layoutRefreshQueued) return;
                _layoutRefreshQueued = true;

                _host.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        _layoutRefreshQueued = false;
                        if (!GetIsEnabled(_host)) return;
                        EnsureScrollViewerHook();
                        RefreshScrollBarHooks();
                        UpdateVisibility();
                    }));
            }

            private void EnsureScrollViewerHook()
            {
                if (_scrollViewer == null && !(_host is ScrollViewer))
                    _scrollViewer = FindVisualChild<ScrollViewer>(_host);

                if (_scrollViewer == null) return;

                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer.ScrollChanged += OnScrollChanged;
            }

            private void DetachScrollViewer()
            {
                if (_scrollViewer == null) return;
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer = null;
            }

            private void OnScrollActivity(object sender, MouseWheelEventArgs e)
            {
                NotifyScrollActivity();
            }

            private void OnPreviewKeyDown(object sender, KeyEventArgs e)
            {
                if (!IsScrollKey(e.Key)) return;
                NotifyScrollActivity();
            }

            private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
            {
                if (e.VerticalChange == 0.0 && e.HorizontalChange == 0.0)
                    return;

                NotifyScrollActivity();
            }

            private void NotifyScrollActivity()
            {
                Expand();
                ScheduleHide();
            }

            private void AttachWindowHandlers()
            {
                DetachWindowHandlers();
                _window = Window.GetWindow(_host);
                if (_window == null) return;

                _window.PreviewMouseMove += OnWindowPreviewMouseMove;
                _window.MouseLeave += OnWindowMouseLeave;
            }

            private void DetachWindowHandlers()
            {
                if (_window == null) return;

                _window.PreviewMouseMove -= OnWindowPreviewMouseMove;
                _window.MouseLeave -= OnWindowMouseLeave;
                _window = null;
            }

            private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
            {
                if (_window == null) return;

                bool inZone = IsMouseNearAnyScrollbar(e.GetPosition(_window));
                if (inZone == _isInProximityZone) return;

                _isInProximityZone = inZone;
                if (inZone)
                {
                    Expand();
                    ScheduleHide();
                    return;
                }

                if (!ShouldStayVisible())
                    ScheduleHide();
            }

            private void OnWindowMouseLeave(object sender, MouseEventArgs e)
            {
                if (!_isInProximityZone) return;

                _isInProximityZone = false;
                if (!ShouldStayVisible())
                    ScheduleHide();
            }

            private bool IsMouseNearAnyScrollbar(Point windowPoint)
            {
                if (_scrollBarHitBounds.Count == 0) return false;

                for (int i = 0; i < _scrollBarHitBounds.Count; i++)
                {
                    if (_scrollBarHitBounds[i].Contains(windowPoint))
                        return true;
                }

                return false;
            }

            private void RebuildScrollBarHitBounds()
            {
                _scrollBarHitBounds.Clear();
                int zone = Math.Max(0, GetProximityZonePixels(_host));
                if (zone == 0 || _window == null) return;

                foreach (var scrollBar in _hookedScrollBars)
                {
                    if (TryCreateScrollBarHitBounds(scrollBar, _window, zone, out ScrollBarHitBounds bounds))
                        _scrollBarHitBounds.Add(bounds);
                }
            }

            private static bool TryCreateScrollBarHitBounds(
                ScrollBar scrollBar,
                Window window,
                int zone,
                out ScrollBarHitBounds bounds)
            {
                bounds = default;

                if (scrollBar == null
                    || window == null
                    || !scrollBar.IsVisible
                    || scrollBar.ActualWidth <= 0
                    || scrollBar.ActualHeight <= 0)
                {
                    return false;
                }

                if (!ReferenceEquals(Window.GetWindow(scrollBar), window))
                    return false;

                try
                {
                    GeneralTransform transform = scrollBar.TransformToVisual(window);
                    if (transform == null) return false;

                    Point topLeft = transform.Transform(new Point(0, 0));
                    double left;
                    double top;
                    double right;
                    double bottom;

                    if (scrollBar.Orientation == Orientation.Vertical)
                    {
                        left = topLeft.X - zone;
                        right = topLeft.X + scrollBar.ActualWidth + zone;
                        top = topLeft.Y;
                        bottom = topLeft.Y + scrollBar.ActualHeight;
                    }
                    else
                    {
                        left = topLeft.X;
                        right = topLeft.X + scrollBar.ActualWidth;
                        top = topLeft.Y - zone;
                        bottom = topLeft.Y + scrollBar.ActualHeight + zone;
                    }

                    bounds = new ScrollBarHitBounds(new Rect(
                        new Point(left, top),
                        new Point(right, bottom)));
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private void RefreshScrollBarHooks()
            {
                var current = new HashSet<ScrollBar>(FindVisualChildren<ScrollBar>(_host));

                var toRemove = new List<ScrollBar>();
                foreach (var scrollBar in _hookedScrollBars)
                {
                    if (!current.Contains(scrollBar))
                        toRemove.Add(scrollBar);
                }

                for (int i = 0; i < toRemove.Count; i++)
                    DetachScrollBarHook(toRemove[i]);

                foreach (var scrollBar in current)
                {
                    if (_hookedScrollBars.Contains(scrollBar)) continue;
                    AttachScrollBarHook(scrollBar);
                }

                RebuildScrollBarHitBounds();
            }

            private void AttachScrollBarHook(ScrollBar scrollBar)
            {
                scrollBar.MouseEnter += OnScrollBarMouseEnter;
                scrollBar.MouseLeave += OnScrollBarMouseLeave;
                scrollBar.PreviewMouseLeftButtonDown += OnScrollBarMouseDown;
                scrollBar.PreviewMouseLeftButtonUp += OnScrollBarMouseDown;
                _hookedScrollBars.Add(scrollBar);
            }

            private void DetachScrollBarHook(ScrollBar scrollBar)
            {
                scrollBar.MouseEnter -= OnScrollBarMouseEnter;
                scrollBar.MouseLeave -= OnScrollBarMouseLeave;
                scrollBar.PreviewMouseLeftButtonDown -= OnScrollBarMouseDown;
                scrollBar.PreviewMouseLeftButtonUp -= OnScrollBarMouseDown;
                _hookedScrollBars.Remove(scrollBar);
            }

            private void DetachScrollBarHooks()
            {
                foreach (var scrollBar in new List<ScrollBar>(_hookedScrollBars))
                    DetachScrollBarHook(scrollBar);
                _hookedScrollBars.Clear();
            }

            private void OnScrollBarMouseEnter(object sender, MouseEventArgs e)
            {
                _isPointerOverScrollbar = true;
                Expand();
                CancelHide();
            }

            private void OnScrollBarMouseLeave(object sender, MouseEventArgs e)
            {
                _isPointerOverScrollbar = false;
                if (!ShouldStayVisible())
                    ScheduleHide();
            }

            private void OnScrollBarMouseDown(object sender, MouseButtonEventArgs e)
            {
                NotifyScrollActivity();
            }

            private bool ShouldStayVisible()
            {
                return _isPointerOverScrollbar || _isInProximityZone;
            }

            private void Expand()
            {
                if (_isExpanded) return;
                _isExpanded = true;
                FadeScrollBars(_host, true);
            }

            private void Collapse(bool immediate = false)
            {
                if (ShouldStayVisible()) return;

                _isExpanded = false;

                if (immediate || GetFadeMilliseconds(_host) == 0)
                {
                    FadeScrollBars(_host, false);
                    return;
                }

                FadeScrollBars(_host, false);
            }

            private void UpdateVisibility()
            {
                if (ShouldStayVisible() || _isExpanded)
                {
                    if (!_isExpanded)
                        Expand();
                    return;
                }

                Collapse();
            }

            private void ScheduleHide()
            {
                if (ShouldStayVisible()) return;

                CancelHide();
                int delay = Math.Max(0, GetHideDelayMilliseconds(_host));
                if (delay == 0)
                {
                    Collapse();
                    return;
                }

                _hideTimer.Interval = TimeSpan.FromMilliseconds(delay);
                _hideTimer.Start();
            }

            private void CancelHide()
            {
                _hideTimer.Stop();
            }

            private void OnHideTimerTick(object sender, EventArgs e)
            {
                CancelHide();
                Collapse();
            }

            private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
            {
                if (root == null) return null;

                int count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(root, i);
                    if (child is T match)
                        return match;

                    T nested = FindVisualChild<T>(child);
                    if (nested != null) return nested;
                }

                return null;
            }

            private struct ScrollBarHitBounds
            {
                private readonly Rect _bounds;

                public ScrollBarHitBounds(Rect bounds)
                {
                    _bounds = bounds;
                }

                public bool Contains(Point point)
                {
                    return _bounds.Contains(point);
                }
            }
        }
    }
}
