using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>Three-row live view: follow the tail, or preserve the reader's older-call scroll position.</summary>
    internal sealed class ToastActivityPanel : Border
    {
        internal const int VisibleEntryCount = 3;
        internal const double EntryHeight = 42;
        private readonly ListBox _list;
        private readonly TextBlock _heading;
        private readonly Func<bool> _motionEnabled;
        private ScrollViewer _scrollViewer;
        private ScrollBar _verticalBar;
        private DispatcherTimer _scrollFadeTimer;
        private bool _scrollExpanded;
        private int _scrollFadeGeneration;
        private int _arrivalGeneration;
        private readonly List<FrameworkElement> _arrivalTargets = new List<FrameworkElement>();
        private Border _arrivalDot;

        /// <summary>True while a newly arrived row is still sliding/fading in.</summary>
        internal bool IsArrivalAnimating { get; private set; }

        public ToastActivityPanel(Func<bool> motionEnabled = null)
        {
            _motionEnabled = motionEnabled ?? (() => SystemParameters.ClientAreaAnimation);
            Margin = new Thickness(24, 6, 0, 0);
            Padding = new Thickness(0, 6, 0, 0);
            BorderBrush = McpToastTheme.ThumbnailBackground;
            BorderThickness = new Thickness(0, 1, 0, 0);
            Cursor = Cursors.Arrow;
            Focusable = false;
            var content = new StackPanel();
            _heading = new TextBlock
            {
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = McpToastTheme.TextSecondary, Margin = new Thickness(0, 0, 0, 4)
            };
            content.Children.Add(_heading);
            var styles = ToastScrollBarStyles.Create();
            _list = new ListBox
            {
                Style = (Style)styles["ToastActivityList"], Height = 0,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemContainerStyle = ContainerStyle(), ItemTemplate = EntryTemplate()
            };
            _list.Resources.MergedDictionaries.Add(styles);
            content.Children.Add(_list);
            Child = content;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            PreviewMouseMove += OnPointerMove;
            PreviewMouseWheel += OnMouseWheel;
        }

        private static readonly DependencyProperty EntriesProperty = DependencyProperty.Register(
            nameof(Entries), typeof(IReadOnlyList<ToastActivityEntry>), typeof(ToastActivityPanel),
            new PropertyMetadata(ToastActivityLog.Freeze(null)));
        public IReadOnlyList<ToastActivityEntry> Entries
        {
            get => (IReadOnlyList<ToastActivityEntry>)GetValue(EntriesProperty);
            private set => SetValue(EntriesProperty, value);
        }

        internal void SetEntries(IReadOnlyList<ToastActivityEntry> entries)
        {
            CancelArrival();
            Entries = entries;
            _heading.Text = L.T("toast.activity.recent", ("count", entries.Count));
            _list.ItemsSource = entries;
            _list.Height = Math.Min(VisibleEntryCount, entries.Count) * EntryHeight;
            if (entries.Count == 0) StopScrollCue();
            InvalidateMeasure();
        }

        internal void RefreshLocalization()
        {
            _heading.Text = L.T("toast.activity.recent", ("count", Entries.Count));
            var offset = _scrollViewer?.VerticalOffset ?? 0;
            // Recreate only the small realized viewport so status automation labels use the new locale.
            _list.ItemTemplate = EntryTemplate();
            _list.UpdateLayout();
            _scrollViewer?.ScrollToVerticalOffset(offset);
        }

        internal void UpdateEntries(IReadOnlyList<ToastActivityEntry> entries)
        {
            if (Entries.Count == entries.Count && (entries.Count == 0
                || ReferenceEquals(Entries[Entries.Count - 1], entries[entries.Count - 1])))
                return;
            EnsureScrollViewer();
            _list.UpdateLayout(); // commit a user's pending wheel/thumb command before deciding to follow
            var offset = _scrollViewer?.VerticalOffset ?? 0;
            var followTail = _scrollViewer == null || _scrollViewer.ScrollableHeight - offset <= 0.5;
            SetEntries(entries);
            _list.UpdateLayout();
            if (followTail)
            {
                ScrollToLatest();
                PlayArrival((_scrollViewer?.VerticalOffset ?? 0) - offset);
            }
            else
            {
                _scrollViewer.ScrollToVerticalOffset(offset);
                _list.UpdateLayout();
                // The reader keeps their place; only hint, through the scroll bar, that the list grew.
                RevealScrollCue();
            }
        }

        /// <summary>
        /// A new result follows the tail: the rows glide up by the distance the list just scrolled,
        /// and the new row fades in while its outcome dot pops. Nothing here changes layout, the
        /// scroll offset or the card size, so it is purely a transform/opacity overlay that is
        /// removed when it ends or when anything else touches the list.
        /// </summary>
        private void PlayArrival(double scrolled)
        {
            if (!_motionEnabled() || !IsVisible || Entries.Count == 0)
                return;
            var last = _list.ItemContainerGenerator.ContainerFromIndex(Entries.Count - 1) as FrameworkElement;
            if (last == null)
                return;

            var generation = ++_arrivalGeneration;
            // Rows were at +scrolled before the jump; with nothing to scroll (list still growing) the new row
            // only rises a few DIPs.
            var shift = Math.Min(Math.Max(scrolled, 0), EntryHeight * VisibleEntryCount);
            var duration = TimeSpan.FromMilliseconds(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            IsArrivalAnimating = true;

            for (var i = 0; i < Entries.Count; i++)
            {
                var row = _list.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (row == null)
                    continue;
                var isNew = ReferenceEquals(row, last);
                var from = isNew ? (shift > 0.5 ? shift : 10) : shift;
                if (from < 0.5)
                    continue;
                var move = new TranslateTransform(0, from);
                row.RenderTransform = move;
                _arrivalTargets.Add(row);
                var slide = new DoubleAnimation(from, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
                if (isNew)
                {
                    slide.Completed += (_, __) =>
                    {
                        if (generation == _arrivalGeneration)
                            CancelArrival();
                    };
                    row.Opacity = 0;
                    row.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                }
                move.BeginAnimation(TranslateTransform.YProperty, slide);
            }

            // Outcome dot: a short overshoot pop so the new result reads as "just landed".
            var dot = FindTagged(last, "OutcomeNode") as Border;
            if (dot != null)
            {
                var scale = new ScaleTransform(0.3, 0.3);
                dot.RenderTransformOrigin = new Point(0.5, 0.5);
                dot.RenderTransform = scale;
                _arrivalDot = dot;
                var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 },
                    FillBehavior = FillBehavior.Stop
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }

        /// <summary>Remove every arrival transform/clock and restore the resting state.</summary>
        internal void CancelArrival()
        {
            ++_arrivalGeneration;
            foreach (var row in _arrivalTargets)
            {
                row.BeginAnimation(OpacityProperty, null);
                row.Opacity = 1;
                if (row.RenderTransform is TranslateTransform move)
                    move.BeginAnimation(TranslateTransform.YProperty, null);
                row.RenderTransform = Transform.Identity;
            }
            _arrivalTargets.Clear();
            if (_arrivalDot != null)
            {
                if (_arrivalDot.RenderTransform is ScaleTransform scale)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                }
                _arrivalDot.RenderTransform = Transform.Identity;
                _arrivalDot = null;
            }
            IsArrivalAnimating = false;
        }

        private static FrameworkElement FindTagged(DependencyObject root, string tag)
        {
            if (root == null)
                return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement element && Equals(element.Tag, tag))
                    return element;
                var found = FindTagged(child, tag);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>Start a new hover at the tail, or continue following after new results.</summary>
        internal void ScrollToLatest()
        {
            if (Entries.Count == 0) return;
            _list.ApplyTemplate();
            _list.UpdateLayout();
            EnsureScrollViewer();
            _scrollViewer?.ScrollToEnd();
            _list.UpdateLayout();
        }

        internal void StopScrollCue()
        {
            _scrollFadeTimer?.Stop();
            _scrollFadeTimer = null;
            _scrollFadeGeneration++;
            _scrollExpanded = false;
            if (_verticalBar != null)
            {
                _verticalBar.BeginAnimation(OpacityProperty, null);
                _verticalBar.Opacity = 0.22;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => EnsureScrollViewer();
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelArrival();
            StopScrollCue();
            if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
            _verticalBar = null;
        }

        private void EnsureScrollViewer()
        {
            if (_scrollViewer != null) return;
            _list.ApplyTemplate();
            _scrollViewer = Find<ScrollViewer>(_list);
            if (_scrollViewer == null) return;
            _scrollViewer.ApplyTemplate();
            _verticalBar = Find<ScrollBar>(_scrollViewer);
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0 && Entries.Count > VisibleEntryCount) RevealScrollCue();
        }
        private void OnMouseWheel(object sender, MouseWheelEventArgs e) => RevealScrollCue();
        private void OnPointerMove(object sender, MouseEventArgs e)
        {
            // Same 28 DIP proximity zone as RVT Settings/History, not generic card hover.
            if (_list.IsMouseOver && e.GetPosition(_list).X >= _list.ActualWidth - 28)
                RevealScrollCue();
        }

        private void RevealScrollCue()
        {
            if (Entries.Count <= VisibleEntryCount || _verticalBar == null) return;
            FadeScrollBar(true);
            if (_scrollFadeTimer == null)
            {
                _scrollFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                _scrollFadeTimer.Tick += (_, __) =>
                {
                    if (_verticalBar != null && (_verticalBar.IsMouseCaptureWithin
                        || (_list.IsMouseOver && Mouse.GetPosition(_list).X >= _list.ActualWidth - 28)))
                        return;
                    _scrollFadeTimer.Stop();
                    FadeScrollBar(false);
                };
            }
            _scrollFadeTimer.Stop();
            _scrollFadeTimer.Start();
        }

        private void FadeScrollBar(bool expanded)
        {
            if (_verticalBar == null || _scrollExpanded == expanded) return;
            _scrollExpanded = expanded;
            var bar = _verticalBar;
            var target = expanded ? 1.0 : 0.22;
            var generation = ++_scrollFadeGeneration;
            if (!_motionEnabled())
            {
                bar.BeginAnimation(OpacityProperty, null);
                bar.Opacity = target;
                return;
            }
            var fade = new DoubleAnimation(bar.Opacity, target, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            fade.Completed += (_, __) =>
            {
                if (generation != _scrollFadeGeneration) return;
                bar.BeginAnimation(OpacityProperty, null);
                bar.Opacity = target;
            };
            bar.BeginAnimation(OpacityProperty, fade);
        }

        private static Style ContainerStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(FocusableProperty, false));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.ContentProperty, new Binding());
            presenter.SetBinding(ContentPresenter.ContentTemplateProperty,
                new Binding("ContentTemplate") { RelativeSource = RelativeSource.TemplatedParent });
            style.Setters.Add(new Setter(Control.TemplateProperty,
                new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter }));
            return style;
        }

        private DataTemplate EntryTemplate()
        {
            bool First(ToastActivityEntry e) => Entries.Count > 0 && ReferenceEquals(e, Entries[0]);
            bool Last(ToastActivityEntry e) => Entries.Count > 0 && ReferenceEquals(e, Entries[Entries.Count - 1]);
            var row = new FrameworkElementFactory(typeof(Grid));
            row.SetValue(FrameworkElement.HeightProperty, 42.0);
            foreach (var top in new[] { true, false })
            {
                var rail = new FrameworkElementFactory(typeof(Border));
                rail.SetValue(FrameworkElement.TagProperty, top ? "TimelineTopRail" : "TimelineBottomRail");
                rail.SetValue(FrameworkElement.WidthProperty, 1.0);
                rail.SetValue(FrameworkElement.HeightProperty, 21.0);
                rail.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 0, 0, 0));
                rail.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                rail.SetValue(FrameworkElement.VerticalAlignmentProperty, top ? VerticalAlignment.Top : VerticalAlignment.Bottom);
                rail.SetValue(Border.BackgroundProperty, McpToastTheme.ActivityBorder);
                rail.SetBinding(UIElement.VisibilityProperty, Project(e => (top ? First(e) : Last(e)) ? Visibility.Collapsed : Visibility.Visible));
                row.AppendChild(rail);
            }
            var dot = new FrameworkElementFactory(typeof(Border));
            dot.SetValue(FrameworkElement.TagProperty, "OutcomeNode");
            dot.SetValue(FrameworkElement.WidthProperty, 7.0);
            dot.SetValue(FrameworkElement.HeightProperty, 7.0);
            dot.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 16, 0, 0));
            dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
            dot.SetBinding(Border.CornerRadiusProperty, Project(e => new CornerRadius(e.Success ? 4 : 1.5)));
            dot.SetBinding(Border.BackgroundProperty, Project(e => e.Success ? McpToastTheme.Primary : McpToastTheme.Error));
            dot.SetBinding(FrameworkElement.ToolTipProperty, Project(e => L.T(e.Success ? "toast.activity.success" : "toast.activity.failed")));
            dot.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, Project(e => L.T(e.Success ? "toast.activity.success" : "toast.activity.failed")));
            row.AppendChild(dot);

            var card = new FrameworkElementFactory(typeof(Border));
            card.SetValue(FrameworkElement.TagProperty, "OutcomeCard");
            card.SetValue(FrameworkElement.HeightProperty, 38.0);
            card.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 4));
            card.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
            card.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            card.SetValue(Border.BackgroundProperty, Brushes.White);
            card.SetValue(Border.BorderBrushProperty, McpToastTheme.ActivityBorder);
            card.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding("Title"));
            card.SetBinding(System.Windows.Automation.AutomationProperties.HelpTextProperty, new Binding("Body"));
            card.SetBinding(FrameworkElement.ToolTipProperty, new Binding("TooltipText"));
            row.AppendChild(card);
            var content = new FrameworkElementFactory(typeof(StackPanel));
            card.AppendChild(content);
            var line = new FrameworkElementFactory(typeof(DockPanel));
            content.AppendChild(line);
            var time = Text(10, McpToastTheme.TextSecondary);
            time.SetValue(DockPanel.DockProperty, Dock.Right);
            time.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 1, 0, 0));
            time.SetValue(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
            time.SetBinding(TextBlock.TextProperty, new Binding("LocalTimeText"));
            line.AppendChild(time);
            var title = Text(12, McpToastTheme.Text);
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            title.SetBinding(TextBlock.TextProperty, new Binding("Title"));
            title.SetBinding(FrameworkElement.ToolTipProperty, new Binding("TooltipText"));
            line.AppendChild(title);
            var body = Text(11, McpToastTheme.TextSecondary);
            body.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            body.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            body.SetValue(FrameworkElement.MaxHeightProperty, 16.0);
            body.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 0));
            body.SetBinding(TextBlock.TextProperty, new Binding("Body"));
            body.SetBinding(FrameworkElement.ToolTipProperty, new Binding("TooltipText"));
            content.AppendChild(body);
            return new DataTemplate(typeof(ToastActivityEntry)) { VisualTree = row };
        }

        private static FrameworkElementFactory Text(double size, Brush color)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.FontSizeProperty, size);
            text.SetValue(TextBlock.ForegroundProperty, color);
            return text;
        }

        // Re-evaluate rails when Entries changes, including an unchanged previous-last item
        // whose container WPF reuses after a live append. No cached endpoint flags on entries.
        private MultiBinding Project(Func<ToastActivityEntry, object> projection)
        {
            var binding = new MultiBinding { Converter = new Projection(projection), Mode = BindingMode.OneWay };
            binding.Bindings.Add(new Binding());
            binding.Bindings.Add(new Binding(nameof(Entries)) { Source = this });
            return binding;
        }

        private sealed class Projection : IMultiValueConverter
        {
            private readonly Func<ToastActivityEntry, object> _project;
            internal Projection(Func<ToastActivityEntry, object> project) { _project = project; }
            public object Convert(object[] values, Type type, object parameter, CultureInfo culture) =>
                values.Length > 0 && values[0] is ToastActivityEntry entry ? _project(entry) : DependencyProperty.UnsetValue;
            public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }

        private static T Find<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var found = Find<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
