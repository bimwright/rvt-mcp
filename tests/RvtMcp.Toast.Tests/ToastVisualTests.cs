using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Toast;

internal static class ToastVisualTests
{
    internal static void Run()
    {
        CheckRollingNumbers();
        CheckBrandReveal();
        CheckHiddenBrand();
        CheckReducedMotionAndStatus();
        CheckPalette();
    }

    private static void CheckRollingNumbers()
    {
        var window = Create();
        try
        {
            window.SetPosition(50, 50);
            window.Show();
            window.PlayEnterAnimation();
            Pump(350);
            var number = Field<RollingToastNumber>(window, "_successCount");
            var failures = Field<RollingToastNumber>(window, "_failedCount");
            var captured = Field<RollingToastNumber>(window, "_captureCount");
            var current = (Viewbox)number.Children[1];
            var outgoing = (Viewbox)number.Children[0];
            var width = window.ActualWidth;
            var height = window.ActualHeight;
            window.Update(new ActivitySnapshot(1, false, 10, 0, 2, "Capture View", "Saved", true, false));
            Pump(45);
            if (number.Value != 10 || captured.Value != 2 || failures.Value != 0)
                throw new Exception("Snapshot counters were not applied.");
            var incomingY = ((TranslateTransform)current.RenderTransform).Y;
            var outgoingY = ((TranslateTransform)outgoing.RenderTransform).Y;
            if (incomingY <= 0 || incomingY >= RollingToastNumber.SlotHeight || outgoingY >= 0)
                throw new Exception($"Expected a vertical roll: outgoing={outgoingY}, incoming={incomingY}.");
            if (((TranslateTransform)((Viewbox)failures.Children[1]).RenderTransform).HasAnimatedProperties)
                throw new Exception("Unchanged counters must not animate.");
            var mask = Field<TextBlock>(window, "_brandText").OpacityMask;
            window.RefreshLocalization();
            if (!ReferenceEquals(mask, Field<TextBlock>(window, "_brandText").OpacityMask))
                throw new Exception("Localization refresh replayed the brand animation.");

            // Replace unfinished rolls; the last value wins, without a queue of animations.
            for (var i = 11; i <= 110; i++)
                window.Update(new ActivitySnapshot(1, false, i, 1, 2, "List Rooms", "Done", true, true));
            Pump(350);
            if (number.Value != 110 || ((TranslateTransform)current.RenderTransform).Y != 0)
                throw new Exception("A burst did not settle at the last counter value.");
            if (Math.Abs(window.ActualWidth - width) > .1 || Math.Abs(window.ActualHeight - height) > .1)
                throw new Exception("Counter changes resized the card.");
            window.Update(new ActivitySnapshot(1, false, int.MaxValue, 1, 2, "List Rooms", "Done", true, true));
            Pump(350);
            if (number.Value != int.MaxValue || Math.Abs(window.ActualWidth - width) > .1)
                throw new Exception("Large counts must fit without widening the card.");
            window.CloseImmediate();
            if (((TranslateTransform)current.RenderTransform).HasAnimatedProperties)
                throw new Exception("Closed window retained counter animation clocks.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: rolling counters, unchanged values, digit growth, burst replacement and cleanup");
    }

    private static void CheckBrandReveal()
    {
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(
            new ActivitySnapshot(1, false, 9, 0, 0, "List Rooms", "Done", true, false),
            null, null, null, null, null, () => cursor, () => true);
        try
        {
            window.SetPosition(50, 50);
            window.CapturePointerBaseline();
            window.Show();
            window.PlayEnterAnimation();
            Pump(180);
            window.UpdateLayout();
            var footer = Field<Grid>(window, "_brandRow");
            var sweep = Field<TranslateTransform>(window, "_brandSweep");
            var reservedHeight = window.ActualHeight;
            if (footer.Visibility != Visibility.Hidden || sweep.HasAnimatedProperties)
                throw new Exception("The wordmark must stay hidden until the pointer moves onto the card.");

            cursor = new Point(24, 10);
            RaisePointer(window, Mouse.MouseEnterEvent);
            Pump(40);
            if (footer.Visibility != Visibility.Hidden)
                throw new Exception("The wordmark must wait briefly so a quick pass does not flash.");

            Pump(140);
            window.UpdateLayout();
            if (footer.Visibility != Visibility.Visible || Math.Abs(window.ActualHeight - reservedHeight) > 1)
                throw new Exception("A real hover must show the wordmark without changing the card height.");
            var baseMask = (LinearGradientBrush)Field<TextBlock>(window, "_brandText").OpacityMask;
            var shineMask = (LinearGradientBrush)Field<TextBlock>(window, "_brandShine").OpacityMask;
            if (baseMask.GradientStops[0].Color.A != 204 || baseMask.GradientStops[4].Color.A != 0)
                throw new Exception("The wipe must settle behind the crest and stay transparent ahead of it.");
            if (baseMask.GradientStops[2].Color.A != 0 || shineMask.GradientStops[2].Color.A != 255)
                throw new Exception("At the sweep crest the bright letters must replace the base letters.");
            for (var i = 0; i < 5; i++)
            {
                if (baseMask.GradientStops[i].Offset != shineMask.GradientStops[i].Offset)
                    throw new Exception("The two letter masks must have aligned crossfade stops.");
            }
            for (var i = 0; i < 3; i++)
            {
                var a = baseMask.GradientStops[i].Color.A / 255.0;
                var b = shineMask.GradientStops[i].Color.A / 255.0;
                if (a + b < .79)
                    throw new Exception("The trailing crossfade must not blank the letters already revealed.");
            }
            window.Update(new ActivitySnapshot(1, false, 10, 0, 0, "List Rooms", "Done", true, false));
            if (!ReferenceEquals(baseMask, Field<TextBlock>(window, "_brandText").OpacityMask))
                throw new Exception("An activity update restarted the brand sweep.");
            Pump(700);
            var shineSweep = Field<TranslateTransform>(window, "_shineSweep");
            if (Math.Abs(sweep.X - .75) > .02 || Math.Abs(shineSweep.X - .75) > .02)
                throw new Exception("The two wordmark layers did not settle together.");

            cursor = new Point(28, 14);
            RaisePointer(window, Mouse.MouseEnterEvent);
            Pump(40);
            if (Math.Abs(sweep.X - .75) > .05)
                throw new Exception("A further hover while the wordmark is up must not replay the wipe.");

            cursor = new Point(90, 90);
            RaisePointer(window, Mouse.MouseLeaveEvent);
            Pump(280);
            window.UpdateLayout();
            if (footer.Visibility != Visibility.Hidden || sweep.HasAnimatedProperties
                || Math.Abs(window.ActualHeight - reservedHeight) > 1)
                throw new Exception("Leaving the card must hide the wordmark without changing the card height.");

            cursor = new Point(30, 16);
            RaisePointer(window, Mouse.MouseEnterEvent);
            Pump(160);
            if (footer.Visibility != Visibility.Visible || !sweep.HasAnimatedProperties)
                throw new Exception("The next hover must reveal the wordmark again.");
            window.CloseImmediate();
            if (sweep.HasAnimatedProperties || shineSweep.HasAnimatedProperties)
                throw new Exception("Close retained brand animation clocks.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: hover wipes the wordmark in, holds it, fades it out, and can reveal it again");
    }

    private static void CheckHiddenBrand()
    {
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(
            new ActivitySnapshot(1, false, 9, 0, 0, "List Rooms", "Done", true, false),
            null, null, null, null, null, () => cursor, () => true);
        try
        {
            window.SetPosition(50, 50);
            window.CapturePointerBaseline();
            window.Show();
            window.PlayEnterAnimation();
            Pump(50);
            var title = Field<TextBlock>(window, "_titleText");
            var footer = Field<Grid>(window, "_brandRow");
            window.UpdateLayout();
            if (title.Text != "RVT-MCP - List Rooms" || footer.Visibility != Visibility.Hidden)
                throw new Exception("Branding on keeps a blank brand row until hover.");
            var reservedHeight = window.ActualHeight;

            cursor = new Point(22, 10);
            RaisePointer(window, Mouse.MouseEnterEvent);
            Pump(180);
            window.UpdateLayout();
            if (footer.Visibility != Visibility.Visible || Math.Abs(window.ActualHeight - reservedHeight) > 1)
                throw new Exception("Hover must show the wordmark without changing the card height.");

            cursor = new Point(80, 80);
            RaisePointer(window, Mouse.MouseLeaveEvent);
            Pump(280);
            window.SetShowBranding(false);
            window.UpdateLayout();
            if (footer.Visibility != Visibility.Collapsed || title.Text != "List Rooms"
                || window.ActualHeight >= reservedHeight - 0.1)
                throw new Exception("Hiding branding must drop the reserved row and the product prefix.");
            cursor = new Point(40, 12);
            RaisePointer(window, Mouse.MouseEnterEvent);
            Pump(200);
            if (footer.Visibility != Visibility.Collapsed)
                throw new Exception("Branding off must ignore hover.");
            window.Update(new ActivitySnapshot(1, false, 10, 0, 0, "Capture View", "Saved", true, false));
            if (title.Text != "Capture View")
                throw new Exception("Later results must stay unprefixed while branding is hidden.");

            cursor = new Point(90, 90);
            RaisePointer(window, Mouse.MouseLeaveEvent);
            window.SetShowBranding(true);
            window.UpdateLayout();
            if (footer.Visibility != Visibility.Hidden || title.Text != "RVT-MCP - Capture View"
                || Math.Abs(window.ActualHeight - reservedHeight) > 1)
                throw new Exception("Turning branding on reserves the brand row and keeps the wordmark hidden.");
            var sweep = Field<TranslateTransform>(window, "_brandSweep");
            if (sweep.HasAnimatedProperties)
                throw new Exception("Turning branding on must not start the wipe by itself.");
            cursor = new Point(26, 16);
            RaisePointer(window, Mouse.MouseEnterEvent);
            Pump(180);
            if (footer.Visibility != Visibility.Visible)
                throw new Exception("Hover after turning branding on must reveal the wordmark.");
            window.CloseImmediate();
            if (sweep.HasAnimatedProperties)
                throw new Exception("Close retained brand animation clocks.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: branding off ignores hover; branding on reveals only while the pointer is on the card");
    }

    private static void CheckReducedMotionAndStatus()
    {
        var window = Create(motion: false);
        try
        {
            window.CapturePointerBaseline();
            window.SetPosition(50, 50); window.Show(); window.PlayEnterAnimation();
            window.Update(new ActivitySnapshot(1, false, 10, 0, 0, "List Rooms", "Done", true, false));
            var number = Field<RollingToastNumber>(window, "_successCount");
            if (window.Opacity != 1 || window.HasAnimatedProperties
                || ((TranslateTransform)((Viewbox)number.Children[1]).RenderTransform).HasAnimatedProperties)
                throw new Exception("Reduced motion must settle the toast and counters without animation.");
            if (Field<Grid>(window, "_brandRow").Visibility != Visibility.Hidden)
                throw new Exception("Reduced motion must keep the wordmark hidden until hover.");
            window.BeginClose();
            if (window.IsVisible)
                throw new Exception("Reduced motion close should be immediate.");
        }
        finally { window.CloseImmediate(); }

        var reducedCursor = new Point(4, 4);
        var reduced = new McpToastWindow(
            new ActivitySnapshot(1, false, 1, 0, 0, "List Rooms", "Done", true, false),
            null, null, null, null, null, () => reducedCursor, () => false);
        try
        {
            reduced.CapturePointerBaseline();
            reduced.Show();
            reducedCursor = new Point(18, 4);
            RaisePointer(reduced, Mouse.MouseEnterEvent);
            var brand = Field<TextBlock>(reduced, "_brandText");
            var sweep = Field<TranslateTransform>(reduced, "_brandSweep");
            if (Field<Grid>(reduced, "_brandRow").Visibility != Visibility.Visible
                || !(brand.OpacityMask is SolidColorBrush solid) || solid.Color.A != 204
                || sweep.HasAnimatedProperties)
                throw new Exception("Reduced motion hover must show the wordmark at 0.8 alpha with no sweep.");
            reducedCursor = new Point(70, 70);
            RaisePointer(reduced, Mouse.MouseLeaveEvent);
            if (Field<Grid>(reduced, "_brandRow").Visibility != Visibility.Hidden)
                throw new Exception("Reduced motion leave must hide the wordmark and keep its row.");
        }
        finally { reduced.CloseImmediate(); }

        var status = new McpToastWindow(new ActivitySnapshot(2, true, 0, 0, 0, "Connected", "Ready", true, false,
            () => new ActivityStatusText("Connected", "Localized ready")), null, null, null, null, null);
        try
        {
            status.RefreshLocalization();
            if (Field<Viewbox>(status, "_counterRow").Visibility != Visibility.Collapsed
                || Field<TextBlock>(status, "_bodyText").Text != "Localized ready")
                throw new Exception("Status toast must retain a localized summary instead of counters.");
        }
        finally { status.CloseImmediate(); }
        Console.WriteLine("PASS: reduced motion and status summary behavior");
    }

    private static void CheckPalette()
    {
        foreach (var kind in new[] { ToolActivityKind.Read, ToolActivityKind.Write })
        {
            var vm = new McpToastViewModel { Success = true, Kind = kind };
            var gradient = (LinearGradientBrush)McpToastTheme.BuildAccentBrush(vm);
            if (gradient.GradientStops[1].Color != McpToastTheme.Primary.Color
                || McpToastTheme.BuildIconBrush(vm) != McpToastTheme.Primary)
                throw new Exception("All successful tool kinds must keep the blue gradient.");
        }
        var error = (LinearGradientBrush)McpToastTheme.BuildAccentBrush(new McpToastViewModel { Success = false });
        if (error.GradientStops[1].Color != McpToastTheme.Error.Color)
            throw new Exception("Errors must keep the red gradient.");
        Console.WriteLine("PASS: blue success (including writes), red failure");
    }

    private static void RaisePointer(McpToastWindow window, RoutedEvent kind)
    {
        window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = kind });
    }

    private static McpToastWindow Create(bool motion = true) => new McpToastWindow(
        new ActivitySnapshot(1, false, 9, 0, 0, "List Rooms", "Done", true, false),
        null, null, null, null, null, () => new Point(-999, -999), () => motion);

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner);

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
