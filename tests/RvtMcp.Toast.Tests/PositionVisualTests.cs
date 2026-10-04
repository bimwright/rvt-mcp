using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Views.Toast;

internal static class PositionVisualTests
{
    public static void Run()
    {
        CheckAnchorsAndExpansion();
        CheckBottomLiveTail();
        CheckDragAndClick();
        CheckMotionAndCleanup();
        Console.WriteLine("PASS: four corners, fixed expansion edge, opposite-side fallback, relative drag/reset, click threshold, owner reflow and motion cleanup");
    }

    private static void CheckAnchorsAndExpansion()
    {
        foreach (var right in new[] { false, true })
        foreach (var bottom in new[] { false, true })
        {
            var options = new ToastPositionOptions(right, bottom);
            var branding = false;
            var cursor = new Point(-1000, -1000);
            var owner = new ToastBounds(100, 100, 1000, 600);
            var work = new ToastBounds(0, 0, 1200, 800);
            var activity = Activity();
            var manager = new McpToastManager(Dispatcher.CurrentDispatcher, activity, motionEnabled: () => false,
                positionOptions: () => options, showBranding: () => branding,
                geometry: () => Tuple.Create(owner, work), cursorPosition: () => cursor);
            manager.Render();
            var window = Window(manager);
            try
            {
                Pump(50);
                var expected = ToastPlacement.Anchor(owner, window.ActualWidth, window.ActualHeight, options);
                Near(window.Left, expected.Left); Near(window.Top, expected.Top);
                var edge = bottom ? window.Top + window.ActualHeight : window.Top;
                var horizontalEdge = right ? window.Left + window.ActualWidth : window.Left;
                Hover(window, ref cursor);
                Pump(270);
                if (Field<Border>(window, "_detailsRow").ActualHeight < 100) throw new Exception("Hover did not expand details.");
                Near(bottom ? window.Top + window.ActualHeight : window.Top, edge);
                Near(right ? window.Left + window.ActualWidth : window.Left, horizontalEdge);
                branding = true; manager.ApplyShowBranding(); Pump(30);
                Near(bottom ? window.Top + window.ActualHeight : window.Top, edge);
                branding = false; manager.ApplyShowBranding(); Pump(30);
                Near(bottom ? window.Top + window.ActualHeight : window.Top, edge);
                window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
                cursor += new Vector(20, 20);
                window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
                Pump(170);
                Near(bottom ? window.Top + window.ActualHeight : window.Top, edge);
                owner = new ToastBounds(150, 120, 900, 550);
                manager.Tick(true);
                expected = ToastPlacement.Anchor(owner, window.ActualWidth, window.ActualHeight, options);
                Near(window.Left, expected.Left); Near(window.Top, expected.Top);

                options = new ToastPositionOptions(right, bottom, true, 0, bottom ? -450 : 450);
                manager.ApplyPosition();
                Pump(30);
                var compactTop = window.Top;
                var compactBottom = window.Top + window.ActualHeight;
                Hover(window, ref cursor);
                Pump(270);
                if (bottom) Near(window.Top, compactTop); // Near the top: bottom preference must expand downward.
                else Near(window.Top + window.ActualHeight, compactBottom); // Near bottom: expand upward.
                if (window.Top < work.Top || window.Top + window.ActualHeight > work.Bottom + .5)
                    throw new Exception("Expansion escaped monitor work area.");
            }
            finally { manager.DismissAllImmediate(); manager.Dispose(); }
        }
    }

    private static void CheckBottomLiveTail()
    {
        var options = new ToastPositionOptions(true, true);
        var cursor = new Point(-1000, -1000);
        var activity = new ActivityAggregator(() => 60);
        activity.RecordResult("First", "Done", true, null, true);
        var manager = new McpToastManager(Dispatcher.CurrentDispatcher, activity, motionEnabled: () => true,
            positionOptions: () => options, cursorPosition: () => cursor,
            geometry: () => Tuple.Create(new ToastBounds(100, 100, 1000, 600), new ToastBounds(0, 0, 1200, 800)));
        manager.Render(); var window = Window(manager); Pump(350);
        try
        {
            var bottom = window.Top + window.ActualHeight;
            var right = window.Left + window.ActualWidth;
            Hover(window, ref cursor); Pump(280);
            Near(window.Top + window.ActualHeight, bottom);
            for (var i = 2; i <= 4; i++)
            {
                activity.RecordResult("Tool " + i, "New result", true, null, true);
                manager.Render(); Pump(90);
                Near(window.Top + window.ActualHeight, bottom);
                Near(window.Left + window.ActualWidth, right);
                Pump(250);
                Near(window.Top + window.ActualHeight, bottom);
            }
            options = options.WithCorner(false, false); manager.ApplyPosition(); Pump(300);
            Near(window.Left, 116); Near(window.Top, 116 + ToastPlacement.RibbonClearance);
        }
        finally { manager.DismissAllImmediate(); manager.Dispose(); }
    }

    private static void CheckDragAndClick()
    {
        var clicks = 0; var saves = 0; var entered = 0; var left = 0;
        var snapshot = Activity().TakeRender().Card;
        var window = new McpToastWindow(snapshot, null, null, _ => clicks++, _ => entered++, _ => left++, motionEnabled: () => false);
        window.SetPosition(100, 100);
        window.Show(); window.PlayEnterAnimation(); Pump(30);
        try
        {
            window.DragCompleted = _ => saves++;
            window.BeginHeaderDrag(new Point(100, 100));
            if (window.IsDragging) throw new Exception("Drag must default off.");
            window.SetPositionPreferences(true, false, false);
            foreach (var field in new[] { "_closeHost", "_detailsRow", "_thumbnailHost" })
            {
                Field<FrameworkElement>(window, field).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                if (window.IsDragging) throw new Exception("Only the title row may start dragging; not " + field);
            }
            window.BeginHeaderDrag(new Point(100, 100));
            if (!window.IsDragging || !window.IsMouseCaptured) throw new Exception("Non-activating mouse capture failed.");
            window.ContinueHeaderDrag(new Point(101, 101));
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            if (clicks != 1 || saves != 0) throw new Exception("Below-threshold gesture must remain a click: clicks=" + clicks + ", saves=" + saves);
            var foreground = GetForegroundWindow();
            window.BeginHeaderDrag(new Point(100, 100));
            window.ContinueHeaderDrag(new Point(200, 200));
            var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
            window.RaiseEvent(release);
            if (!release.Handled || saves != 1 || clicks != 1 || window.IsMouseCaptured || window.IsDragging)
                throw new Exception("Drag release must persist once without dismissing or retaining capture.");
            if (GetForegroundWindow() != foreground) throw new Exception("Drag stole foreground focus.");
            if (entered < 2 || (!window.IsMouseOver && left < 2)) throw new Exception("Drag must pause/rearm idle even outside the card.");
            window.BeginHeaderDrag(new Point(200, 200));
            window.ContinueHeaderDrag(new Point(300, 300));
            window.ReleaseMouseCapture();
            if (window.IsDragging || saves != 1) throw new Exception("Lost capture must cancel without saving.");
            window.BeginHeaderDrag(new Point(300, 300));
            window.CloseImmediate();
            if (window.IsMouseCaptured || window.IsDragging || window.DragCompleted != null)
                throw new Exception("Close retained drag state or callbacks.");
        }
        finally { window.CloseImmediate(); }

        var options = new ToastPositionOptions(dragEnabled: true);
        var activity = Activity();
        var owner = new ToastBounds(100, 100, 1000, 600);
        var work = new ToastBounds(0, 0, 1200, 800);
        var manager = new McpToastManager(Dispatcher.CurrentDispatcher, activity, motionEnabled: () => false,
            positionOptions: () => options, positionChanged: value => { options = value; saves++; }, geometry: () => Tuple.Create(owner, work));
        manager.Render(); window = Window(manager); Pump(30);
        try
        {
            window.BeginHeaderDrag(new Point(100, 100));
            window.ContinueHeaderDrag(new Point(200, 180));
            if (!window.FinishHeaderDrag() || !options.HasOffset) throw new Exception("Manager did not store relative offset.");
            var offsetX = options.OffsetX; var offsetY = options.OffsetY;
            var dragLeft = window.Left; var dragTop = window.Top;
            options = options.WithDrag(false); manager.ApplyPosition(); Pump(20);
            Near(window.Left, 116); Near(window.Top, 116 + ToastPlacement.RibbonClearance);
            if (options.OffsetX != offsetX || options.OffsetY != offsetY) throw new Exception("Disabling drag erased offset.");
            options = options.WithDrag(true); manager.ApplyPosition(); Pump(20);
            Near(window.Left, dragLeft); Near(window.Top, dragTop);
            owner = new ToastBounds(150, 150, 1000, 600); manager.Tick(true);
            Near(window.Left, dragLeft + 50); Near(window.Top, dragTop + 50);
            options = options.WithOffset(null, null); manager.ApplyPosition(); Pump(20);
            Near(window.Left, 166); Near(window.Top, 166 + ToastPlacement.RibbonClearance);
            options = new ToastPositionOptions(true, true, true, 100000, -100000); manager.ApplyPosition(); Pump(20);
            if (window.Left < 0 || window.Top < 0 || window.Left + window.ActualWidth > work.Right + .5)
                throw new Exception("Restored out-of-monitor offset was not clamped.");
        }
        finally { manager.DismissAllImmediate(); manager.Dispose(); }
    }

    private static void CheckMotionAndCleanup()
    {
        var options = new ToastPositionOptions();
        var cursor = new Point(-1000, -1000);
        var activity = Activity();
        var manager = new McpToastManager(Dispatcher.CurrentDispatcher, activity, motionEnabled: () => true,
            positionOptions: () => options, cursorPosition: () => cursor,
            geometry: () => Tuple.Create(new ToastBounds(100, 100, 1000, 600), new ToastBounds(0, 0, 1200, 800)));
        manager.Render(); var window = Window(manager); Pump(350);
        try
        {
            var original = window.Left;
            options = new ToastPositionOptions(true, true);
            manager.ApplyPosition();
            Pump(80);
            var target = 1100 - 16 - window.ActualWidth;
            if (window.Left <= original || window.Left >= target || !window.IsPositionAnimating)
                throw new Exception("Corner changes must slide through intermediate positions.");
            Pump(250);
            Near(window.Left, target);
            Near(window.Top + window.ActualHeight, 684);
            if (window.IsPositionAnimating) throw new Exception("Position animation did not settle.");
            var slide = Field<TranslateTransform>(window, "_slideTransform");
            window.PlayEnterAnimation();
            for (var i = 0; i < 8 && slide.X <= 0; i++) Pump(20);
            if (slide.X <= 0) throw new Exception("Right-aligned toast must enter from right.");
            Pump(300);
            Hover(window, ref cursor); Pump(235);
            options = new ToastPositionOptions(); manager.ApplyPosition(); Pump(80);
            if (!window.IsPositionAnimating) throw new Exception("Changing corners during expansion cancelled movement.");
            Pump(600); Near(window.Left, 116); Near(window.Top, 116 + ToastPlacement.RibbonClearance);
            options = new ToastPositionOptions(true, true); manager.ApplyPosition(); Pump(350);
            window.BeginClose();
            // Poll for the first animation frame; a fixed short pump can sample before it renders.
            for (var i = 0; i < 8 && slide.X <= 0; i++) Pump(20);
            if (slide.X <= 0) throw new Exception("Right-aligned toast must exit to right.");
            manager.DismissAllImmediate();
            if (window.IsPositionAnimating || window.PrepareExpansion != null || window.ConstrainDrag != null)
                throw new Exception("Forced close retained positioning callbacks/clocks.");
        }
        finally { manager.DismissAllImmediate(); manager.Dispose(); }
    }

    private static ActivityAggregator Activity()
    {
        var activity = new ActivityAggregator(() => 60);
        for (var i = 0; i < 5; i++) activity.RecordResult("Tool " + i, "Result " + i, true, null, true);
        return activity;
    }
    private static void Hover(McpToastWindow window, ref Point cursor)
    {
        cursor += new Vector(25, 25);
        window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
        window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseMoveEvent });
    }
    private static McpToastWindow Window(McpToastManager manager) => Field<McpToastWindow>(manager, "_window");
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
    private static void Near(double actual, double expected)
    {
        if (Math.Abs(actual - expected) > .7) throw new Exception("Position mismatch: " + actual + " vs " + expected);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
