using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Toast;

// A new result glides in: rows slide up by the scrolled distance, the new row fades in and its
// outcome dot pops. Only transform/opacity overlays; layout, offset and card size must not change.
internal static class ArrivalVisualTests
{
    internal static void Run()
    {
        CheckFollowingTailPlaysAndSettlesClean();
        CheckReducedMotionAndReaderPositionHaveNoEffect();
        CheckBackToBackAndCloseLeaveNothingBehind();
    }

    private static Point _cursor = new Point(10, 10);

    private static McpToastWindow Open(ActivityAggregator activity, Func<bool> motion, out ToastActivityPanel panel)
    {
        _cursor = new Point(10, 10);
        var window = new McpToastWindow(activity.TakeRender().Card, null, null, null, null, null, () => _cursor, motion);
        window.CapturePointerBaseline(); window.SetPosition(50, 50); window.Show(); window.PlayEnterAnimation();
        panel = Children<ToastActivityPanel>(window).Single();
        return window;
    }

    private static void CheckFollowingTailPlaysAndSettlesClean()
    {
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero);
        for (var i = 0; i < 6; i++) activity.RecordResult("Tool " + i, "Result " + i, true, null, true);
        var window = Open(activity, () => true, out var panel);
        try
        {
            _cursor = new Point(20, 10); Enter(window);
            var list = Children<ListBox>(window).Single(); var viewer = Children<ScrollViewer>(list).Single();
            WaitForFrame(() => list.Items.Count == 6 && list.ActualHeight >= 125, "details opened");
            Pump(350);
            var height = window.ActualHeight;
            activity.RecordResult("Incoming", "Just landed", true, null, true);
            window.Update(activity.TakeRender().Card);
            window.UpdateLayout();
            if (!panel.IsArrivalAnimating) throw new Exception("A following reader got no arrival effect.");
            if (Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) > .1)
                throw new Exception("Arrival effect must not change the followed scroll offset.");
            var items = Children<ListBoxItem>(list).ToList();
            var newest = items.Single(item => ((ToastActivityEntry)item.DataContext).Title == "Incoming");
            var older = items.First(item => ((ToastActivityEntry)item.DataContext).Title == "Tool 5");
            WaitForFrame(() => newest.Opacity < 1 || newest.RenderTransform is TranslateTransform t && t.Y > 0, "arrival started");
            if (!(newest.RenderTransform is TranslateTransform move) || move.Y <= 0)
                throw new Exception("The new row must rise from below.");
            if (!(older.RenderTransform is TranslateTransform shift) || shift.Y <= 0)
                throw new Exception("Older rows must glide up by the scrolled distance.");
            if (Math.Abs(window.ActualHeight - height) > .1)
                throw new Exception("Arrival effect must not resize the card.");
            WaitForFrame(() => !panel.IsArrivalAnimating, "arrival settled");
            foreach (var item in Children<ListBoxItem>(list))
                if (item.Opacity != 1 || item.RenderTransform != Transform.Identity && !(item.RenderTransform is MatrixTransform m && m.Matrix.IsIdentity))
                    throw new Exception("Arrival left a residual opacity/transform on a row.");
            var dot = Children<Border>(newest).Single(b => Equals(b.Tag, "OutcomeNode"));
            if (!(dot.RenderTransform == Transform.Identity || dot.RenderTransform.Value.IsIdentity))
                throw new Exception("Arrival left a residual transform on the outcome dot.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: following reader gets a glide-up/fade-in/dot-pop arrival that settles with no residue and no layout change");
    }

    private static void CheckReducedMotionAndReaderPositionHaveNoEffect()
    {
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero);
        for (var i = 0; i < 12; i++) activity.RecordResult("Tool " + i, "Result " + i, true, null, true);
        var motion = false;
        var window = Open(activity, () => motion, out var panel);
        try
        {
            _cursor = new Point(20, 10); Enter(window); Pump(250);
            var list = Children<ListBox>(window).Single(); var viewer = Children<ScrollViewer>(list).Single();
            activity.RecordResult("Quiet", "No motion", true, null, true);
            window.Update(activity.TakeRender().Card);
            if (panel.IsArrivalAnimating) throw new Exception("Reduced motion must show the new row directly.");
            motion = true;
            viewer.ScrollToVerticalOffset(84); Pump(60);
            var offset = viewer.VerticalOffset;
            activity.RecordResult("While reading", "Hold position", true, null, true);
            window.Update(activity.TakeRender().Card); window.UpdateLayout();
            if (panel.IsArrivalAnimating) throw new Exception("A reader of older calls must not get a sliding effect.");
            if (Math.Abs(viewer.VerticalOffset - offset) > .1)
                throw new Exception("A new result moved a reader's scroll position.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: reduced motion and a scrolled-up reader get no arrival effect and keep their position");
    }

    private static void CheckBackToBackAndCloseLeaveNothingBehind()
    {
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero);
        for (var i = 0; i < 6; i++) activity.RecordResult("Tool " + i, "Result " + i, true, null, true);
        var window = Open(activity, () => true, out var panel);
        var list = default(ListBox);
        try
        {
            _cursor = new Point(20, 10); Enter(window);
            list = Children<ListBox>(window).Single();
            WaitForFrame(() => list.Items.Count == 6 && list.ActualHeight >= 125, "details opened");
            Pump(350);
            for (var i = 0; i < 4; i++)
            {
                activity.RecordResult("Burst " + i, i % 2 == 0 ? "ok" : "failed", i % 2 == 0, null, true);
                window.Update(activity.TakeRender().Card); window.UpdateLayout();
                Pump(40);
            }
            WaitForFrame(() => !panel.IsArrivalAnimating, "burst settled");
            foreach (var item in Children<ListBoxItem>(list))
                if (item.Opacity != 1) throw new Exception("Back-to-back arrivals left a half-transparent row.");
            activity.RecordResult("Last", "Closing", true, null, true);
            window.Update(activity.TakeRender().Card);
            if (!panel.IsArrivalAnimating) throw new Exception("Expected an in-flight arrival to interrupt.");
        }
        finally { window.CloseImmediate(); }
        if (panel.IsArrivalAnimating) throw new Exception("Closing the card left an arrival animation running.");
        Console.WriteLine("PASS: back-to-back arrivals restart cleanly and a forced close cancels the effect");
    }

    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }

    private static void Enter(McpToastWindow window) => window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = UIElement.MouseEnterEvent });

    private static void WaitForFrame(Func<bool> condition, string phase)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Timed out waiting for " + phase + ".");
            Pump(8);
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
