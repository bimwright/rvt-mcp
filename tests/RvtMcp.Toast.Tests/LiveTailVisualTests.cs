using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Toast;

// Behavior seam: real result snapshots -> McpToastWindow.Update -> visible WPF rows/scroll position.
// Only clocks/cursor/motion are injected. No private-method calls or mocked internal collaborators.
internal static class LiveTailVisualTests
{
    internal static void Run()
    {
        CheckNewestResultAppearsWhilePointerStaysInside();
        CheckOlderScrollPositionAndResumeTail();
        CheckOneToThreeRowsWhileHeld();
        CheckIncomingDuringMotionAndClosedState();
    }

    private static void CheckNewestResultAppearsWhilePointerStaysInside()
    {
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero);
        for (var i = 0; i < 5; i++) activity.RecordResult("Tool " + i, "Result " + i, true, null, true);
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(activity.TakeRender().Card, null, null, null, null, null,
            () => cursor, () => false);
        try
        {
            window.CapturePointerBaseline(); window.SetPosition(50, 50); window.Show(); window.PlayEnterAnimation();
            cursor = new Point(20, 10); Enter(window); Pump(250);
            var list = Children<ListBox>(window).Single();
            var viewer = Children<ScrollViewer>(list).Single();
            ExpectRows(list, viewer, "Tool 2", "Tool 3", "Tool 4");
            var height = window.ActualHeight;
            var width = window.ActualWidth;
            activity.RecordResult("Incoming", "Arrived without mouse leave", true, null, true);
            window.Update(activity.TakeRender().Card); Pump(60);
            if (list.Items.Count != 6)
                throw new Exception("A held-open toast did not receive the new result: visible collection count=" + list.Items.Count);
            ExpectRows(list, viewer, "Tool 3", "Tool 4", "Incoming");
            if (Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) > .1
                || Math.Abs(window.ActualHeight - height) > .1 || Math.Abs(window.ActualWidth - width) > .1)
                throw new Exception("Live-tail must follow the newest rows without changing the three-row card size.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: held pointer receives new results immediately and follows the newest three, no hover re-entry or size jump");
    }

    private static void CheckOlderScrollPositionAndResumeTail()
    {
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero);
        for (var i = 0; i < 20; i++) activity.RecordResult("Tool " + i, "Result " + i, true, null, true);
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(activity.TakeRender().Card, null, null, null, null, null, () => cursor, () => false);
        try
        {
            window.CapturePointerBaseline(); window.Show(); window.PlayEnterAnimation();
            cursor = new Point(20, 10); Enter(window); Pump(250);
            var list = Children<ListBox>(window).Single(); var viewer = Children<ScrollViewer>(list).Single();
            viewer.ScrollToVerticalOffset(220.5); Pump(40); // partway through an older row, not an item boundary
            var offset = viewer.VerticalOffset;
            var before = VisibleRows(list, viewer);
            for (var i = 20; i < 80; i++) activity.RecordResult("Tool " + i, "Result " + i, true, null, true);
            window.Update(activity.TakeRender().Card); Pump(60);
            if (list.Items.Count != 80 || Math.Abs(viewer.VerticalOffset - offset) > .1
                || !VisibleRows(list, viewer).SequenceEqual(before))
                throw new Exception("A result burst moved the older-call reader or failed to append new calls.");
            viewer.ScrollToTop(); Pump(40);
            activity.RecordResult("Top incoming", "Done", true, null, true);
            window.Update(activity.TakeRender().Card); Pump(60);
            ExpectRows(list, viewer, "Tool 0", "Tool 1", "Tool 2");
            if (viewer.VerticalOffset != 0 || list.Items.Count != 81)
                throw new Exception("Reading at offset zero was mistaken for following the tail.");
            viewer.ScrollToEnd(); Pump(40);
            activity.RecordResult("Resumed", "Done", true, null, true);
            window.Update(activity.TakeRender().Card); Pump(60);
            ExpectRows(list, viewer, "Tool 79", "Top incoming", "Resumed");
            if (Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) > .1)
                throw new Exception("Returning to the bottom did not resume live-tail.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: older fractional/top scroll stays in place while results append; returning to the bottom resumes live-tail");
    }

    private static void CheckOneToThreeRowsWhileHeld()
    {
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero);
        activity.RecordResult("Tool 0", "Done", true, null, true);
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(activity.TakeRender().Card, null, null, null, null, null, () => cursor, () => false);
        try
        {
            window.CapturePointerBaseline(); window.Show(); window.PlayEnterAnimation();
            cursor = new Point(20, 10); Enter(window); Pump(250);
            var list = Children<ListBox>(window).Single(); var viewer = Children<ScrollViewer>(list).Single();
            var oneRowHeight = window.ActualHeight;
            for (var i = 1; i <= 3; i++)
            {
                activity.RecordResult("Tool " + i, "Done", true, null, true);
                window.Update(activity.TakeRender().Card); Pump(60);
                var expectedHeight = oneRowHeight + Math.Min(i, 2) * 42;
                if (Math.Abs(window.ActualHeight - expectedHeight) > .1)
                    throw new Exception("A live result was clipped during one/two/three-row growth: expected height="
                        + expectedHeight + ", got=" + window.ActualHeight);
            }
            ExpectRows(list, viewer, "Tool 1", "Tool 2", "Tool 3");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: a held single-call toast grows to two/three complete rows, then keeps the three-row height");
    }

    internal static void CheckIncomingDuringMotionAndClosedState()
    {
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero);
        activity.RecordResult("First", "Done", true, null, true);
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(activity.TakeRender().Card, null, null, null, null, null, () => cursor, () => true);
        try
        {
            window.CapturePointerBaseline(); window.Show(); window.PlayEnterAnimation();
            var compactHeight = window.ActualHeight;
            var panel = Children<ToastActivityPanel>(window).Single();
            var row = (Border)VisualTreeHelper.GetParent(panel);
            cursor = new Point(20, 10); Enter(window);
            WaitForFrame(() => row.Height > 0 && row.HasAnimatedProperties, "first row opening");
            var list = Children<ListBox>(window).Single(); var viewer = Children<ScrollViewer>(list).Single();
            for (var i = 0; i < 1000; i++) activity.RecordResult("Burst " + i, "Done", true, null, true);
            window.Update(activity.TakeRender().Card);
            WaitForFrame(() => row.Height > 0 && !row.HasAnimatedProperties, "burst opening settled");
            window.UpdateLayout();
            ExpectRows(list, viewer, "Burst 997", "Burst 998", "Burst 999");
            var fullHeight = window.ActualHeight;
            if (list.Items.Count != 1001 || viewer.ViewportHeight != 126
                || Children<ListBoxItem>(list).Count() > 8 || fullHeight <= compactHeight)
                throw new Exception("Incoming burst during opening failed to settle at a virtualized three-row view.");
            viewer.ScrollToVerticalOffset(220.5); Pump(40);
            var olderOffset = viewer.VerticalOffset;
            var olderRows = VisibleRows(list, viewer);
            var openRowHeight = row.Height;
            cursor = new Point(90, 90); Leave(window);
            // Observe an actual in-flight close. A fixed sleep may overshoot it on a busy dispatcher.
            WaitForFrame(() => row.HasAnimatedProperties && row.Height > 0 && row.Height < openRowHeight - 1,
                "interrupted collapse");
            var interruptedHeight = window.ActualHeight;
            var interruptedCount = list.Items.Count;
            activity.RecordResult("During close", "Done", false, null, true);
            window.Update(activity.TakeRender().Card);
            cursor = new Point(22, 10); Enter(window);
            WaitForFrame(() => row.Height > 0 && !row.HasAnimatedProperties, "reversal settled");
            window.UpdateLayout();
            if (list.Items.Count != 1002 || Math.Abs(viewer.VerticalOffset - olderOffset) > .1
                || !VisibleRows(list, viewer).SequenceEqual(olderRows) || Math.Abs(window.ActualHeight - fullHeight) > .1)
                throw new Exception("Incoming result plus interrupted collapse: count=" + list.Items.Count
                    + ", offset=" + viewer.VerticalOffset + " expected=" + olderOffset
                    + ", rows=[" + string.Join(", ", VisibleRows(list, viewer)) + "] expected=[" + string.Join(", ", olderRows)
                    + "], height=" + window.ActualHeight + " expected=" + fullHeight
                    + ", interrupted height=" + interruptedHeight + " compact=" + compactHeight + ", count=" + interruptedCount);
            cursor = new Point(90, 90); Leave(window);
            WaitForFrame(() => row.Visibility == Visibility.Collapsed && !row.HasAnimatedProperties, "fully collapsed");
            activity.RecordResult("While collapsed", "Done", true, null, true);
            window.Update(activity.TakeRender().Card); Pump(100);
            if (list.Items.Count != 0 || Math.Abs(window.ActualHeight - compactHeight) > .1)
                throw new Exception("A collapsed toast retained detail rows or auto-expanded on an incoming result.");
            cursor = new Point(24, 10); Enter(window);
            WaitForFrame(() => row.Height > 0 && !row.HasAnimatedProperties, "next hover settled");
            window.UpdateLayout();
            ExpectRows(list, viewer, "Burst 999", "During close", "While collapsed");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: live burst retargets opening; incoming during interrupted collapse preserves reading; closed cards stay compact and reopen at latest");
    }

    private static void ExpectRows(ListBox list, ScrollViewer viewer, params string[] expected)
    {
        var actual = VisibleRows(list, viewer);
        if (!actual.SequenceEqual(expected))
            throw new Exception("Expected visible rows [" + string.Join(", ", expected) + "], got [" + string.Join(", ", actual) + "]");
    }
    private static string[] VisibleRows(ListBox list, ScrollViewer viewer) => Enumerable.Range(0, list.Items.Count)
        .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem)
        .Where(item => item != null && item.TransformToAncestor(viewer).Transform(new Point()).Y >= -.1
            && item.TransformToAncestor(viewer).Transform(new Point()).Y + item.ActualHeight <= viewer.ActualHeight + .1)
        .Select(item => ((ToastActivityEntry)item.DataContext).Title).ToArray();
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
    private static void Enter(McpToastWindow window) => window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = Mouse.MouseEnterEvent });
    private static void Leave(McpToastWindow window) => window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = Mouse.MouseLeaveEvent });
    private static void WaitForFrame(Func<bool> condition, string phase)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        var clock = Stopwatch.StartNew();
        var reached = false;
        EventHandler render = (_, __) =>
        {
            if (!condition()) return;
            reached = true;
            frame.Continue = false;
        };
        timeout.Tick += (_, __) => frame.Continue = false;
        CompositionTarget.Rendering += render;
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); CompositionTarget.Rendering -= render; }
        if (!reached) throw new Exception("Did not observe " + phase + " within " + clock.ElapsedMilliseconds + " ms.");
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
