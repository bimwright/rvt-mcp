using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Toast;

internal static class RecentActivityVisualTests
{
    internal static void Run()
    {
        CheckIntentAndSmoothMotion();
        CheckLiveUpdatesAndInterruptedLeave();
        CheckReducedMotionAndCleanup();
        CheckAnimationCleanup();
        CheckStationaryMoveAndStatus();
        CheckCompactBorderedCards();
        CheckAllCallsScrollingAndVirtualization();
        CheckScrollbarMotionAndReducedMotion();
    }

    private static ActivitySnapshot Snapshot(string newest = "Hide Items") => new ActivitySnapshot(
        1, false, 4, 1, 0, newest, "Hidden 24 items", true, true,
        recentEntries: Array.AsReadOnly(new[]
        {
            new ToastActivityEntry("Get Document Info", "2 loaded models", true, 90),
            new ToastActivityEntry("Send Code", "Compile error: missing return value", false, 123),
            new ToastActivityEntry("Find Items", "24 matching items", true, 830),
            new ToastActivityEntry("Select Items By Search", "Selected 12 items", true, 250),
            new ToastActivityEntry(newest, "Hidden 24 items", true, 325)
        }));

    private static void CheckIntentAndSmoothMotion()
    {
        var cursor = new Point(10, 10);
        var clicks = 0;
        var window = new McpToastWindow(Snapshot(), null, null, _ => clicks++, null, null,
            () => cursor, () => true);
        try
        {
            window.SetShowBranding(false);
            window.CapturePointerBaseline();
            window.SetPosition(50, 50); window.Show(); window.PlayEnterAnimation();
            Pump(350);
            var row = Field<Border>(window, "_detailsRow");
            var panel = Field<ToastActivityPanel>(window, "_detailsPanel");
            var width = window.ActualWidth;
            var compactHeight = window.ActualHeight;
            Raise(window, Mouse.MouseEnterEvent); // card under a stationary cursor
            Pump(250);
            if (row.Visibility != Visibility.Collapsed)
                throw new Exception("A stationary pointer exposed recent activity.");

            cursor = new Point(20, 10); Raise(window, Mouse.MouseEnterEvent);
            Pump(40);
            cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent);
            Pump(250);
            if (row.Visibility != Visibility.Collapsed)
                throw new Exception("A quick pointer pass flashed recent activity.");

            cursor = new Point(24, 10); Raise(window, Mouse.MouseEnterEvent);
            Pump(80);
            if (row.Height != 0)
                throw new Exception("Recent activity did not wait for the intent delay.");
            var open = Sample(() => row.Height, () => row.Height > 0 && !row.HasAnimatedProperties);
            var expanded = row.Height;
            AssertGradual(open, 0, expanded, "Details opening");
            window.UpdateLayout();
            if (panel.Entries.Count != 5 || panel.Opacity != 1 || expanded <= 30
                || window.ActualHeight <= compactHeight + 30 || Math.Abs(window.ActualWidth - width) > .1)
                throw new Exception("Details must expand vertically, show five outcomes, and work with branding off.");
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            row.RaiseEvent(click);
            if (!click.Handled || clicks != 0)
                throw new Exception("Reading the detail panel invoked the card dismiss action.");

            cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent);
            Pump(40);
            if (Math.Abs(row.Height - expanded) > .1)
                throw new Exception("Leaving must allow a short boundary-crossing grace period.");
            var close = Sample(() => row.Height, () => row.Visibility == Visibility.Collapsed);
            AssertGradual(close, expanded, 0, "Details closing");
            window.UpdateLayout();
            if (Math.Abs(window.ActualHeight - compactHeight) > 1 || panel.HasAnimatedProperties)
                throw new Exception("Collapsed details retained height or animation clocks.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: deliberate hover, no quick-pass flash, gradual open/close, fixed width and non-dismissable details");
    }

    private static void CheckLiveUpdatesAndInterruptedLeave()
    {
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(Snapshot(), null, null, null, null, null, () => cursor, () => true);
        try
        {
            window.CapturePointerBaseline(); window.SetPosition(50, 50); window.Show();
            cursor = new Point(20, 10); Raise(window, Mouse.MouseEnterEvent); Pump(600);
            var panel = Field<ToastActivityPanel>(window, "_detailsPanel");
            var row = Field<Border>(window, "_detailsRow");
            var height = row.Height;
            for (var i = 0; i < 20; i++) window.Update(Snapshot("New tool " + i));
            Pump(100);
            if (panel.Entries[panel.Entries.Count - 1].Title != "New tool 19"
                || row.HasAnimatedProperties || Math.Abs(row.Height - height) > .1)
                throw new Exception("A held-open result burst did not refresh without replaying the entrance.");
            var entries = panel.Entries;

            // Re-enter during the grace period: keep the same preview without animation.
            cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent); Pump(40);
            cursor = new Point(22, 10); Raise(window, Mouse.MouseEnterEvent); Pump(160);
            if (row.HasAnimatedProperties || !ReferenceEquals(entries, panel.Entries))
                throw new Exception("Boundary re-entry restarted or replaced the preview.");

            // Re-enter during the close: reverse from the current height, never snap to zero.
            cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent);
            Sample(() => row.Height, () => row.Height > 0 && row.Height < height - 1);
            var midway = row.Height;
            cursor = new Point(24, 10); Raise(window, Mouse.MouseEnterEvent);
            if (Math.Abs(row.Height - midway) > 1)
                throw new Exception("Reversing a close jumped the panel height.");
            Pump(350);
            if (Math.Abs(row.Height - height) > 1 || !ReferenceEquals(entries, panel.Entries))
                throw new Exception("Interrupted close did not restore the current preview.");

            cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent); Pump(450);
            cursor = new Point(26, 10); Raise(window, Mouse.MouseEnterEvent); Pump(600);
            if (panel.Entries[panel.Entries.Count - 1].Title != "New tool 19")
                throw new Exception("A new reading session did not take the latest outcomes.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: live held-open updates, boundary grace, continuous close reversal and next-hover refresh");
    }

    private static void CheckReducedMotionAndCleanup()
    {
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(Snapshot(), null, null, null, null, null, () => cursor, () => false);
        try
        {
            window.CapturePointerBaseline(); window.SetPosition(50, 50); window.Show();
            cursor = new Point(20, 10); Raise(window, Mouse.MouseEnterEvent); Pump(250);
            var row = Field<Border>(window, "_detailsRow");
            var panel = Field<ToastActivityPanel>(window, "_detailsPanel");
            var shift = Field<TranslateTransform>(window, "_detailsShift");
            if (row.Height <= 0 || panel.Opacity != 1 || row.HasAnimatedProperties || shift.HasAnimatedProperties)
                throw new Exception("Reduced motion must preserve intent delay but skip expansion animation.");
            cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent); Pump(170);
            if (row.Visibility != Visibility.Collapsed || row.HasAnimatedProperties)
                throw new Exception("Reduced motion retained closing animation.");
            cursor = new Point(22, 10); Raise(window, Mouse.MouseEnterEvent);
            window.CloseImmediate(); Pump(300);
            if (Field<DispatcherTimer>(window, "_detailsRevealTimer") != null
                || Field<DispatcherTimer>(window, "_detailsHideTimer") != null
                || row.HasAnimatedProperties || panel.HasAnimatedProperties || shift.HasAnimatedProperties
                || panel.Entries.Count != 0)
                throw new Exception("Close retained preview timers, animation clocks or outcomes.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: reduced motion, pending-reveal cancellation and preview cleanup");
    }

    private static void CheckAnimationCleanup()
    {
        foreach (var closeWhileHiding in new[] { false, true })
        {
            var cursor = new Point(10, 10);
            var window = new McpToastWindow(Snapshot(), null, null, null, null, null,
                () => cursor, () => true);
            try
            {
                window.CapturePointerBaseline(); window.Show();
                cursor = new Point(20, 10); Raise(window, Mouse.MouseEnterEvent);
                var row = Field<Border>(window, "_detailsRow");
                var panel = Field<ToastActivityPanel>(window, "_detailsPanel");
                var shift = Field<TranslateTransform>(window, "_detailsShift");
                Sample(() => row.Height, () => row.HasAnimatedProperties && row.Height > 0);
                if (closeWhileHiding)
                {
                    Pump(350);
                    cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent);
                    Sample(() => row.Height, () => row.HasAnimatedProperties);
                }
                window.CloseImmediate(); Pump(300);
                if (row.HasAnimatedProperties || panel.HasAnimatedProperties || shift.HasAnimatedProperties
                    || Field<ActivitySnapshot>(window, "_lastSnapshot") != null
                    || Field<DispatcherTimer>(window, "_detailsRevealTimer") != null
                    || Field<DispatcherTimer>(window, "_detailsHideTimer") != null)
                    throw new Exception("Close during a detail animation retained clocks, timers or a snapshot.");
            }
            finally { window.CloseImmediate(); }
        }
        Console.WriteLine("PASS: force-close during opening and closing cancels clocks, timers and retained snapshots");
    }

    private static void CheckStationaryMoveAndStatus()
    {
        var cursor = new Point(10, 10);
        var enters = 0;
        var window = new McpToastWindow(Snapshot(), null, null, null, _ => enters++, null,
            () => cursor, () => false);
        try
        {
            window.CapturePointerBaseline(); window.Show(); Raise(window, Mouse.MouseEnterEvent);
            cursor = new Point(12, 10); Raise(window, Mouse.MouseMoveEvent); Pump(250);
            if (enters == 0 || Field<Border>(window, "_detailsRow").Height == 0)
                throw new Exception("First movement on a card under the cursor must pause idle and reveal outcomes.");
        }
        finally { window.CloseImmediate(); }
        var status = new McpToastWindow(new ActivitySnapshot(2, true, 0, 0, 0, "Connected", "Ready", true, false),
            null, null, null, null, null, () => cursor, () => false);
        try
        {
            status.CapturePointerBaseline(); status.Show();
            cursor = new Point(20, 10); Raise(status, Mouse.MouseEnterEvent); Pump(250);
            if (Field<Border>(status, "_detailsRow").Visibility != Visibility.Collapsed)
                throw new Exception("A connection/toggle status exposed an empty activity panel.");
        }
        finally { status.CloseImmediate(); }
        Console.WriteLine("PASS: first actual mouse movement reveals details; status cards stay compact");
    }

    private static void CheckCompactBorderedCards()
    {
        foreach (var count in new[] { 1, 3, 5 })
        {
            var entries = Enumerable.Range(0, count).Select(i => new ToastActivityEntry("Tool " + i,
                string.Concat(Enumerable.Repeat("A long matching outcome with descenders gyp and details. ", 10)), i != 1)).ToArray();
            var snapshot = new ActivitySnapshot(1, false, count, 0, 0, entries[count - 1].Title,
                entries[count - 1].Body, true, false, recentEntries: entries);
            var cursor = new Point(10, 10);
            var window = new McpToastWindow(snapshot, null, null, null, null, null, () => cursor, () => false);
            try
            {
                window.CapturePointerBaseline(); window.SetPosition(50, 50); window.Show(); window.UpdateLayout();
                var compactHeight = window.ActualHeight;
                var width = window.ActualWidth;
                cursor = new Point(20, 10); Raise(window, Mouse.MouseEnterEvent); Pump(300);
                window.UpdateLayout();
                var panel = Field<ToastActivityPanel>(window, "_detailsPanel");
                var list = Field<ListBox>(panel, "_list");
                var viewer = Field<ScrollViewer>(panel, "_scrollViewer");
                if (((StackPanel)panel.Child).Children.Count != 2)
                    throw new Exception("The activity panel must contain only its heading and list, with no scroll/refresh footer.");
                if (list.Height != Math.Min(3, count) * 42 || window.ActualHeight - compactHeight > 160
                    || Math.Abs(window.ActualWidth - width) > .1)
                    throw new Exception("Bordered activity cards must use compact 42 DIP slots without changing toast width.");
                if (VisibleEntries(list, viewer).Length != Math.Min(3, count))
                    throw new Exception("Compact bordered cards do not fit completely in the viewport.");
                for (var i = Math.Max(0, count - 3); i < count; i++)
                {
                    var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i);
                    var card = VisualChildren<Border>(container).Single(b =>
                        System.Windows.Automation.AutomationProperties.GetName(b) == entries[i].Title);
                    if (card.BorderThickness != new Thickness(1) || card.CornerRadius != new CornerRadius(4)
                        || card.ActualHeight > 38.1 || card.Margin != new Thickness(16, 0, 0, 4)
                        || !(card.BorderBrush is SolidColorBrush stroke)
                        || stroke.Color != (Color)ColorConverter.ConvertFromString("#D8DEE8"))
                        throw new Exception("Activity entries need a thin rounded neutral border and compact card height.");
                    var body = VisualChildren<TextBlock>(card).Single(t => t.Text == entries[i].Body);
                    var title = VisualChildren<TextBlock>(card).Single(t => t.Text == entries[i].Title);
                    var bodyBottom = body.TransformToAncestor(card).Transform(new Point(0, body.ActualHeight)).Y;
                    if (body.TextWrapping != TextWrapping.NoWrap || body.TextTrimming != TextTrimming.CharacterEllipsis
                        || !Equals(body.ToolTip, entries[i].Body) || body.ActualHeight < 14
                        || bodyBottom > card.ActualHeight - card.BorderThickness.Bottom - card.Padding.Bottom + .1
                        || title.ActualHeight < 15)
                        throw new Exception("Compact cards must show title plus one unclipped result line, with full result in the tooltip.");
                }
            }
            finally { window.CloseImmediate(); }
        }
        Console.WriteLine("PASS: 1/3/5-call cards have 1 DIP rounded borders, 42 DIP slots, unclipped one-line results, no footer and unchanged width");
    }

    private static void CheckAllCallsScrollingAndVirtualization()
    {
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero);
        for (var i = 0; i < 5000; i++)
            activity.RecordResult("Tool " + i, "Result " + i, true, null, true, durationMs: 830);
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(activity.TakeRender().Card, null, null, null, null, null,
            () => cursor, () => true);
        try
        {
            window.CapturePointerBaseline(); window.SetPosition(50, 50); window.Show();
            cursor = new Point(20, 10); Raise(window, Mouse.MouseEnterEvent);
            var stressRow = Field<Border>(window, "_detailsRow");
            var stressMotion = Sample(() => stressRow.Height, () => stressRow.Height > 0 && !stressRow.HasAnimatedProperties);
            AssertGradual(stressMotion, 0, stressRow.Height, "Details opening with 5,000 calls");
            Pump(100);
            var panel = Field<ToastActivityPanel>(window, "_detailsPanel");
            var row = Field<Border>(window, "_detailsRow");
            var list = Field<ListBox>(panel, "_list");
            var viewer = Field<ScrollViewer>(panel, "_scrollViewer");
            var bar = Field<System.Windows.Controls.Primitives.ScrollBar>(panel, "_verticalBar");
            if (list.Items.Count != 5000 || list.Height != 3 * ToastActivityPanel.EntryHeight
                || Math.Abs(viewer.ViewportHeight - list.Height) > .1
                || viewer.ScrollableHeight <= 0 || Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) > .1)
                throw new Exception("All calls must be retained with exactly three rows, initially scrolled to the newest.");
            var visible = VisibleEntries(list, viewer);
            if (!visible.SequenceEqual(new[] { "Tool 4997", "Tool 4998", "Tool 4999" }))
                throw new Exception("Newest three entries are not fully visible: " + string.Join(", ", visible));
            var realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
            if (realized > 16 || !System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(list))
                throw new Exception("Large activity lists created too many controls: " + realized);
            if (bar == null || Math.Abs(bar.ActualWidth - 8) > .1 || bar.Focusable)
                throw new Exception("Scrollbar must follow the RVT 8 DIP non-focusable pattern.");
            var track = (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
            track.Thumb.ApplyTemplate();
            var thumbBorder = (Border)track.Thumb.Template.FindName("ThumbBorder", track.Thumb);
            if (thumbBorder.CornerRadius != new CornerRadius(4) || thumbBorder.Margin != new Thickness(1)
                || ((SolidColorBrush)track.Thumb.Background).Color != (Color)ColorConverter.ConvertFromString("#CBD5E1"))
                throw new Exception("Scrollbar thumb differs from the RVT ribbon style.");
            if (!VisualChildren<TextBlock>(list).Any(t => t.Text == panel.Entries[4999].LocalTimeText)
                || VisualChildren<TextBlock>(list).Any(t => t.Text == "830 ms" || t.Text == "0.8 s"))
                throw new Exception("Rows must display local HH:mm:ss completion times, not duration.");
            var height = row.Height;
            viewer.ScrollToTop(); Pump(100);
            if (!VisibleEntries(list, viewer).SequenceEqual(new[] { "Tool 0", "Tool 1", "Tool 2" }))
                throw new Exception("Scrolling up did not reach the oldest retained calls.");
            activity.RecordResult("Incoming", "New result", true, null, true);
            window.Update(activity.TakeRender().Card); Pump(100);
            if (panel.Entries.Count != 5001 || panel.Entries[5000].Title != "Incoming"
                || viewer.VerticalOffset != 0 || row.Height != height)
                throw new Exception("Incoming results were not appended while preserving the older-call scroll position.");
            var entries = panel.Entries;
            cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent);
            Sample(() => row.Height, () => row.Height > 0 && row.Height < height - 1);
            cursor = new Point(22, 10); Raise(window, Mouse.MouseEnterEvent); Pump(350);
            if (viewer.VerticalOffset != 0 || !ReferenceEquals(entries, panel.Entries))
                throw new Exception("Close reversal reset the reader to the bottom.");
            cursor = new Point(90, 90); Raise(window, Mouse.MouseLeaveEvent); Pump(450);
            cursor = new Point(24, 10); Raise(window, Mouse.MouseEnterEvent); Pump(650);
            if (panel.Entries.Count != 5001 || panel.Entries[5000].Title != "Incoming"
                || Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) > .1 || Math.Abs(row.Height - height) > .1)
                throw new Exception("Next hover did not refresh all calls at the same three-row height.");
            window.CloseImmediate(); Pump(250);
            if (Field<DispatcherTimer>(panel, "_scrollFadeTimer") != null || bar.HasAnimatedProperties
                || panel.Entries.Count != 0 || list.Items.Count != 0)
                throw new Exception("Close retained scrollbar timers, clocks or activity items.");
            Console.WriteLine("PASS: 5,001 retained calls; exactly 3 visible; oldest reachable; virtualized containers="
                + realized + "; RVT 8 DIP rounded overlay scrollbar; local HH:mm:ss; live append/scroll retention/reversal/cleanup");
        }
        finally { window.CloseImmediate(); }
    }

    private static void CheckScrollbarMotionAndReducedMotion()
    {
        foreach (var motion in new[] { true, false })
        {
            var cursor = new Point(10, 10);
            var window = new McpToastWindow(Snapshot(), null, null, null, null, null, () => cursor, () => motion);
            try
            {
                window.CapturePointerBaseline(); window.Show();
                cursor = new Point(20, 10); Raise(window, Mouse.MouseEnterEvent); Pump(600);
                var panel = Field<ToastActivityPanel>(window, "_detailsPanel");
                var bar = Field<System.Windows.Controls.Primitives.ScrollBar>(panel, "_verticalBar");
                panel.StopScrollCue();
                panel.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                if (motion)
                {
                    var fadeIn = Sample(() => bar.Opacity, () => !bar.HasAnimatedProperties && Math.Abs(bar.Opacity - 1) < .001);
                    AssertGradual(fadeIn, .22, 1, "Scrollbar reveal");
                    typeof(ToastActivityPanel).GetMethod("FadeScrollBar", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(panel, new object[] { false });
                    var fadeOut = Sample(() => bar.Opacity, () => !bar.HasAnimatedProperties && Math.Abs(bar.Opacity - .22) < .001);
                    AssertGradual(fadeOut, 1, .22, "Scrollbar settle");
                }
                else if (bar.Opacity != 1 || bar.HasAnimatedProperties)
                    throw new Exception("Reduced motion must skip scrollbar animations.");
                window.CloseImmediate(); Pump(200);
                if (Field<DispatcherTimer>(panel, "_scrollFadeTimer") != null || bar.HasAnimatedProperties)
                    throw new Exception("Closing retained a scrollbar timer or fade clock.");
            }
            finally { window.CloseImmediate(); }
        }
        Console.WriteLine("PASS: RVT-style 160 ms scrollbar fades have intermediate frames, settle at 0.22, respect reduced motion and clean up");
    }

    private static string[] VisibleEntries(ListBox list, ScrollViewer viewer) => Enumerable.Range(0, list.Items.Count)
        .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem)
        .Where(item => item != null && item.TransformToAncestor(viewer).Transform(new Point()).Y >= -.1
            && item.TransformToAncestor(viewer).Transform(new Point()).Y + item.ActualHeight <= viewer.ActualHeight + .1)
        .Select(item => ((ToastActivityEntry)item.DataContext).Title).ToArray();

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }

    private static List<double> Sample(Func<double> read, Func<bool> done)
    {
        var values = new List<double> { read() };
        if (!done())
        {
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            EventHandler rendered = (_, __) =>
            {
                values.Add(read());
                if (done()) frame.Continue = false;
            };
            timeout.Tick += (_, __) => frame.Continue = false;
            CompositionTarget.Rendering += rendered;
            try { timeout.Start(); Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); CompositionTarget.Rendering -= rendered; }
        }
        if (!done()) throw new Exception("Detail animation did not settle within two seconds.");
        values.Add(read()); return values;
    }

    private static void AssertGradual(List<double> values, double from, double to, string label)
    {
        var sign = Math.Sign(to - from);
        var margin = Math.Max(.02, Math.Abs(to - from) * .03);
        if (!values.Any(v => Math.Abs(v - from) > margin && Math.Abs(v - to) > margin))
            throw new Exception(label + " jumped without intermediate frames: " + string.Join(", ", values));
        for (var i = 1; i < values.Count; i++)
            if ((values[i] - values[i - 1]) * sign < -.1 || values[i] < Math.Min(from, to) - .1
                || values[i] > Math.Max(from, to) + .1)
                throw new Exception(label + " moved backwards or overshot.");
        Console.WriteLine("MOTION: " + label + "; samples=" + values.Count + "; intermediate="
            + values.Count(v => Math.Abs(v - from) > margin && Math.Abs(v - to) > margin)
            + "; monotonic, no overshoot, endpoints settled");
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    private static void Raise(McpToastWindow window, RoutedEvent kind) => window.RaiseEvent(
        new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = kind });
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
