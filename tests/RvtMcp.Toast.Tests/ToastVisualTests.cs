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
        CheckBrandReplay();
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

    private static void CheckBrandReplay()
    {
        var window = Create();
        try
        {
            window.SetPosition(50, 50);
            window.Show();
            window.PlayEnterAnimation();
            // Complete the initial reveal before triggering the real hover event.
            Pump(2250);
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            var baseMask = (LinearGradientBrush)Field<TextBlock>(window, "_brandText").OpacityMask;
            var shineMask = (LinearGradientBrush)Field<TextBlock>(window, "_brandShine").OpacityMask;
            if (baseMask.GradientStops[0].Color.A != 204 || baseMask.GradientStops[4].Color.A != 204)
                throw new Exception("Hover must keep both sides of the sweep at settled opacity, not reset the whole logo to dim.");
            if (baseMask.GradientStops[2].Color.A != 0 || shineMask.GradientStops[2].Color.A != 255)
                throw new Exception("At the sweep crest the bright letters must replace the base letters.");
            for (var i = 0; i < 5; i++)
            {
                if (baseMask.GradientStops[i].Offset != shineMask.GradientStops[i].Offset)
                    throw new Exception("The two letter masks must have aligned crossfade stops.");
                var a = baseMask.GradientStops[i].Color.A / 255.0;
                var b = shineMask.GradientStops[i].Color.A / 255.0;
                if (a + b < .79)
                    throw new Exception("The crossfade must not blank a section of the logo.");
            }
            window.Update(new ActivitySnapshot(1, false, 10, 0, 0, "List Rooms", "Done", true, false));
            if (!ReferenceEquals(baseMask, Field<TextBlock>(window, "_brandText").OpacityMask))
                throw new Exception("An activity update restarted the brand sweep.");
            Pump(1000);
            var sweep = Field<TranslateTransform>(window, "_brandSweep");
            var shineSweep = Field<TranslateTransform>(window, "_shineSweep");
            if (Math.Abs(sweep.X - .75) > .01 || Math.Abs(shineSweep.X - .75) > .01)
                throw new Exception("The two wordmark layers did not settle together.");
            // A second hover must replace the first pass rather than stacking clocks.
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            window.CloseImmediate();
            if (sweep.HasAnimatedProperties || shineSweep.HasAnimatedProperties)
                throw new Exception("Close retained brand animation clocks.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: two-layer hover crossfade, stable background brightness, no update replay, clock cleanup");
    }

    private static void CheckReducedMotionAndStatus()
    {
        var window = Create(motion: false);
        try
        {
            window.SetPosition(50, 50); window.Show(); window.PlayEnterAnimation();
            window.Update(new ActivitySnapshot(1, false, 10, 0, 0, "List Rooms", "Done", true, false));
            var number = Field<RollingToastNumber>(window, "_successCount");
            if (window.Opacity != 1 || window.HasAnimatedProperties
                || ((TranslateTransform)((Viewbox)number.Children[1]).RenderTransform).HasAnimatedProperties)
                throw new Exception("Reduced motion must settle the toast and counters without animation.");
            var brand = Field<TextBlock>(window, "_brandText");
            if (!(brand.OpacityMask is SolidColorBrush solid) || solid.Color.A != 204)
                throw new Exception("Reduced motion brand must settle directly at 0.8 alpha.");
            window.BeginClose();
            if (window.IsVisible)
                throw new Exception("Reduced motion close should be immediate.");
        }
        finally { window.CloseImmediate(); }

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
