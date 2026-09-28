using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Toast;

internal static class ToastVisualTests
{
    internal static void Run()
    {
        CheckRollingNumbers();
        CheckOdometerRolling();
        CheckThumbnailFrame();
        CheckThumbnailMotion();
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
            var width = window.ActualWidth;
            var height = window.ActualHeight;
            window.Update(new ActivitySnapshot(1, false, 10, 0, 2, "Capture View", "Saved", true, false));
            Pump(45);
            if (number.Value != 10 || captured.Value != 2 || failures.Value != 0)
                throw new Exception("Snapshot counters were not applied.");
            if (!number.IsRolling || number.RollProgress <= 0 || number.RollProgress >= 1)
                throw new Exception($"Expected a vertical roll in flight, got progress {number.RollProgress}.");
            if (failures.IsRolling)
                throw new Exception("Unchanged counters must not animate.");
            var mask = Field<TextBlock>(window, "_brandText").OpacityMask;
            window.RefreshLocalization();
            if (!ReferenceEquals(mask, Field<TextBlock>(window, "_brandText").OpacityMask))
                throw new Exception("Localization refresh replayed the brand animation.");

            // A burst keeps retargeting one roll; the last value wins, without a queue of animations.
            for (var i = 11; i <= 110; i++)
                window.Update(new ActivitySnapshot(1, false, i, 1, 2, "List Rooms", "Done", true, true));
            Pump(900);
            if (number.Value != 110 || number.IsRolling || number.ColumnCount != 3 || number.RollPosition != 110)
                throw new Exception("A burst did not settle at the last counter value.");
            if (Math.Abs(window.ActualWidth - width) > .1 || Math.Abs(window.ActualHeight - height) > .1)
                throw new Exception("Counter changes resized the card.");
            window.Update(new ActivitySnapshot(1, false, int.MaxValue, 1, 2, "List Rooms", "Done", true, true));
            Pump(900);
            if (number.Value != int.MaxValue || number.ColumnCount != 10 || number.IsRolling
                || Math.Abs(window.ActualWidth - width) > .1)
                throw new Exception("Large counts must fit without widening the card.");
            window.CloseImmediate();
            if (number.IsRolling)
                throw new Exception("Closed window retained counter animation clocks.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: rolling counters, unchanged values, digit growth, burst settling and cleanup");
    }

    private static void CheckOdometerRolling()
    {
        var window = Create();
        try
        {
            window.SetPosition(50, 50);
            window.Show();
            window.PlayEnterAnimation();
            Pump(350);
            var number = Field<RollingToastNumber>(window, "_successCount"); // starts at 9

            // Gaining a digit opens the new column gradually instead of shifting the row in one step.
            window.UpdateLayout();
            var oneDigit = number.ActualWidth;
            window.Update(new ActivitySnapshot(1, false, 10, 0, 0, "List Rooms", "Done", true, false));
            var widths = Sample(() => { window.UpdateLayout(); return number.ActualWidth; }, () => !number.IsRolling);
            var twoDigits = widths[widths.Count - 1];
            if (twoDigits < oneDigit + 4)
                throw new Exception($"A second digit must widen the number ({oneDigit} -> {twoDigits}).");
            AssertGradual(widths, oneDigit, twoDigits, "The new digit column");
            if (number.RollPosition != 10)
                throw new Exception("The number did not settle after gaining a digit.");

            // Only the digits that change move: 10 -> 11 rolls the ones column, 19 -> 20 rolls both.
            window.Update(new ActivitySnapshot(1, false, 11, 0, 0, "List Rooms", "Done", true, false));
            if (number.RollingColumnCount != 1)
                throw new Exception($"10 -> 11 must roll only the ones digit, {number.RollingColumnCount} columns rolled.");
            Pump(900);
            window.Update(new ActivitySnapshot(1, false, 19, 0, 0, "List Rooms", "Done", true, false));
            Pump(900);
            window.Update(new ActivitySnapshot(1, false, 20, 0, 0, "List Rooms", "Done", true, false));
            if (number.RollingColumnCount != 2)
                throw new Exception($"19 -> 20 must roll both digits, {number.RollingColumnCount} columns rolled.");
            Pump(900);

            // A result that arrives mid-roll continues from where the digit is; it never jumps back.
            window.Update(new ActivitySnapshot(1, false, 21, 0, 0, "List Rooms", "Done", true, false));
            var clock = Stopwatch.StartNew();
            while (number.RollPosition < 20.15 && clock.ElapsedMilliseconds < 1000)
                Pump(5);
            var before = number.RollPosition;
            if (before < 20.15 || before > 20.97)
                throw new Exception($"Expected to catch the ones digit mid-roll, at {before}.");
            window.Update(new ActivitySnapshot(1, false, 22, 0, 0, "List Rooms", "Done", true, false));
            var after = number.RollPosition;
            if (Math.Abs(after - before) > 0.02)
                throw new Exception($"A new value must continue the roll, not restart it ({before} -> {after}).");
            var positions = Sample(() => number.RollPosition, () => !number.IsRolling);
            AssertGradual(positions, after, 22, "The retargeted roll");
            if (number.RollPosition != 22)
                throw new Exception("The roll did not settle on the newest value.");

            // A jump that adds several digits rolls each new digit in by one step, not through the alphabet.
            window.Update(new ActivitySnapshot(1, false, 4000, 0, 0, "List Rooms", "Done", true, false));
            if (number.ColumnCount != 4 || number.RollingColumnCount != 4)
                throw new Exception($"22 -> 4000 must move all four columns ({number.RollingColumnCount} of {number.ColumnCount}).");
            Sample(() => number.RollPosition, () => !number.IsRolling);
            if (number.RollPosition != 4000)
                throw new Exception("The number did not settle on the jump.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: odometer digits widen gradually, only changed digits roll, and a roll continues on retarget");
    }

    private const double ThumbnailRow = 126; // 6 gap + 120 frame

    /// <summary>Pumps in short steps until <paramref name="done"/>, recording <paramref name="read"/> after each one.</summary>
    private static List<double> Sample(Func<double> read, Func<bool> done, int maxMilliseconds = 2000)
    {
        var values = new List<double> { read() };
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < maxMilliseconds)
        {
            Pump(8);
            values.Add(read());
        }
        if (!done())
            throw new Exception("The animation did not finish in time.");
        values.Add(read());
        return values;
    }

    /// <summary>A gradual move: only ever toward <paramref name="to"/>, never past it, with values in between.</summary>
    private static void AssertGradual(List<double> samples, double from, double to, string what, double endTolerance = -1)
    {
        var trace = string.Join(", ", samples.Select(v => v.ToString("0.##")));
        var sign = Math.Sign(to - from);
        var margin = Math.Max(0.02, Math.Abs(to - from) * 0.03); // "in between" means clear of both ends
        var between = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var v = samples[i];
            if ((v - from) * sign < -0.01 || (to - v) * sign < -0.01)
                throw new Exception($"{what} went outside {from} .. {to}: [{trace}].");
            if (i > 0 && (v - samples[i - 1]) * sign < -0.01)
                throw new Exception($"{what} moved backwards: [{trace}].");
            if (Math.Abs(v - from) > margin && Math.Abs(v - to) > margin)
                between++;
        }
        if (between == 0)
            throw new Exception($"{what} jumped from {from} to {to} without passing through anything in between: [{trace}].");
        if (Math.Abs(samples[samples.Count - 1] - to) > (endTolerance >= 0 ? endTolerance : margin))
            throw new Exception($"{what} ended at {samples[samples.Count - 1]}, not {to}: [{trace}].");
    }

    private static string WritePng(string name, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 0x60; pixels[i + 1] = 0x50; pixels[i + 2] = 0x40;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        var path = Path.Combine(Path.GetTempPath(), name);
        using (var stream = File.Create(path))
            encoder.Save(stream);
        return path;
    }

    private static ActivitySnapshot CaptureSnapshot(string image) =>
        new ActivitySnapshot(1, false, 10, 0, image == null ? 0 : 1, "Capture View", "Saved", true, false, imagePath: image);

    private static void CheckThumbnailFrame()
    {
        var wide = WritePng("rvtmcp-toast-frame-wide.png", 400, 100);
        var tall = WritePng("rvtmcp-toast-frame-tall.png", 100, 400);
        var window = Create(motion: false);
        try
        {
            window.SetPosition(50, 50);
            window.Show();
            Pump(50);
            var host = Field<Border>(window, "_thumbnailHost");
            var image = Field<Image>(window, "_thumbnailImage");
            Size? frame = null;
            double? cardHeight = null;
            foreach (var path in new[] { wide, tall })
            {
                window.Update(CaptureSnapshot(path));
                window.UpdateLayout();
                var centre = image.TranslatePoint(new Point(image.ActualWidth / 2, image.ActualHeight / 2), host);
                if (Math.Abs(centre.X - host.ActualWidth / 2) > 0.6 || Math.Abs(centre.Y - host.ActualHeight / 2) > 0.6)
                    throw new Exception($"The capture must be centred on both axes: image centre {centre}, frame {host.ActualWidth}x{host.ActualHeight}.");
                var padding = host.Padding.Left + host.BorderThickness.Left;
                if (image.ActualWidth > host.ActualWidth - 2 * padding + 0.5 || image.ActualHeight > host.ActualHeight - 2 * padding + 0.5)
                    throw new Exception("The capture must fit inside the frame.");
                // Letterboxed, not stretched: the wide one fills the width, the tall one the height.
                var fillsWidth = Math.Abs(image.ActualWidth - (host.ActualWidth - 2 * padding)) < 0.6;
                var fillsHeight = Math.Abs(image.ActualHeight - (host.ActualHeight - 2 * padding)) < 0.6;
                if (path == wide ? !fillsWidth || fillsHeight : fillsWidth || !fillsHeight)
                    throw new Exception("The capture must keep its aspect ratio inside the frame.");
                var size = new Size(host.ActualWidth, host.ActualHeight);
                if (frame != null && (Math.Abs(frame.Value.Width - size.Width) > 0.1 || Math.Abs(frame.Value.Height - size.Height) > 0.1))
                    throw new Exception("The frame must keep one size for every capture shape.");
                if (cardHeight != null && Math.Abs(cardHeight.Value - window.ActualHeight) > 0.1)
                    throw new Exception("The card must not change height between capture shapes.");
                frame = size;
                cardHeight = window.ActualHeight;
            }
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: thumbnail is centred on both axes in one fixed frame, whatever the capture shape");
    }

    private static void CheckThumbnailMotion()
    {
        var first = WritePng("rvtmcp-toast-motion-a.png", 320, 200);
        var second = WritePng("rvtmcp-toast-motion-b.png", 200, 320);
        var window = Create();
        try
        {
            window.SetPosition(50, 50);
            window.Show();
            window.PlayEnterAnimation();
            Pump(350);
            var row = Field<Border>(window, "_thumbnailRow");
            var host = Field<Border>(window, "_thumbnailHost");
            var front = Field<Image>(window, "_thumbnailImage");
            var back = Field<Image>(window, "_thumbnailBack");
            window.UpdateLayout();
            var closed = window.ActualHeight;
            bool Animated() => row.HasAnimatedProperties || host.HasAnimatedProperties
                || front.HasAnimatedProperties || back.HasAnimatedProperties;
            double CardHeight() { window.UpdateLayout(); return window.ActualHeight; }

            // Opens from nothing: the row grows, the card follows it, and the frame fades in.
            window.Update(CaptureSnapshot(first));
            var fadeIn = new List<double>();
            var opening = Sample(() =>
            {
                fadeIn.Add(host.Opacity);
                return CardHeight();
            }, () => !Animated());
            AssertGradual(opening, closed, closed + ThumbnailRow, "Opening the thumbnail");
            AssertGradual(fadeIn, 0, 1, "Fading the thumbnail in");
            if (row.Height != ThumbnailRow || host.Opacity != 1 || front.Source == null)
                throw new Exception("The thumbnail did not settle open.");
            var open = CardHeight();

            // A newer capture cross-fades over the old one and the card keeps its size.
            window.Update(CaptureSnapshot(second));
            var drift = 0.0;
            var backSeen = false;
            var backFade = new List<double>();
            var incoming = Sample(() =>
            {
                drift = Math.Max(drift, Math.Abs(CardHeight() - open));
                if (back.Source != null)
                {
                    backSeen = true;
                    backFade.Add(back.Opacity);
                }
                return front.Opacity;
            }, () => !Animated());
            AssertGradual(incoming, 0, 1, "The new capture fading in");
            // Sampled only while the old capture is on screen, so the last sample is just short of 0.
            AssertGradual(backFade, 1, 0, "The old capture fading out", endTolerance: 0.25);
            if (!backSeen || back.Source != null || front.Source == null || front.Opacity != 1)
                throw new Exception("The cross-fade did not settle on the new capture.");
            if (drift > 0.5)
                throw new Exception("Swapping captures resized the card.");

            // Closes: the frame fades first, then the card closes up around the gap.
            window.Update(CaptureSnapshot(null));
            var fadeOut = new List<double>();
            var shrankBeforeFading = false;
            var closing = Sample(() =>
            {
                var height = CardHeight();
                if (row.Visibility == Visibility.Visible)
                {
                    fadeOut.Add(host.Opacity);
                    if (height < open - 1 && host.Opacity >= 1)
                        shrankBeforeFading = true;
                }
                return height;
            }, () => row.Visibility == Visibility.Collapsed && !Animated());
            AssertGradual(closing, open, closed, "Closing the card up");
            AssertGradual(fadeOut, 1, 0, "Fading the thumbnail out");
            if (shrankBeforeFading)
                throw new Exception("The card started closing before the thumbnail had begun to fade.");
            if (front.Source != null || back.Source != null)
                throw new Exception("The closed thumbnail still holds its images.");

            // Closing and reopening in quick succession ends fully open, not half-way.
            window.Update(CaptureSnapshot(first));
            Pump(60);
            window.Update(CaptureSnapshot(null));
            Pump(60);
            window.Update(CaptureSnapshot(second));
            Sample(CardHeight, () => !Animated());
            if (row.Visibility != Visibility.Visible || row.Height != ThumbnailRow || host.Opacity != 1
                || front.Source == null || back.Source != null)
                throw new Exception("An interrupted close must still end fully open.");

            window.CloseImmediate();
            if (Animated())
                throw new Exception("Closed window retained thumbnail animation clocks.");
        }
        finally { window.CloseImmediate(); }

        var still = Create(motion: false);
        try
        {
            still.SetPosition(50, 50);
            still.Show();
            var row = Field<Border>(still, "_thumbnailRow");
            still.Update(CaptureSnapshot(first));
            if (row.Visibility != Visibility.Visible || row.Height != ThumbnailRow || row.HasAnimatedProperties)
                throw new Exception("Reduced motion must open the thumbnail at once.");
            still.Update(CaptureSnapshot(null));
            if (row.Visibility != Visibility.Collapsed || row.HasAnimatedProperties)
                throw new Exception("Reduced motion must close the thumbnail at once.");
        }
        finally { still.CloseImmediate(); }
        Console.WriteLine("PASS: thumbnail opens, cross-fades and closes gradually, survives interruption, and is instant without motion");
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
            var tool = Field<TextBlock>(window, "_toolText");
            var footer = Field<Grid>(window, "_brandRow");
            window.UpdateLayout();
            if (title.Text != "rvt-mcp" || tool.Text != "List Rooms" || footer.Visibility != Visibility.Hidden)
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
            if (footer.Visibility != Visibility.Collapsed || title.Text != "rvt-mcp"
                || window.ActualHeight >= reservedHeight - 0.1)
                throw new Exception("Hiding branding must drop the reserved row and keep the title.");
            cursor = new Point(40, 12);
            RaisePointer(window, Mouse.MouseEnterEvent);
            Pump(200);
            if (footer.Visibility != Visibility.Collapsed)
                throw new Exception("Branding off must ignore hover.");
            window.Update(new ActivitySnapshot(1, false, 10, 0, 0, "Capture View", "Saved", true, false));
            if (title.Text != "rvt-mcp" || tool.Text != "Capture View")
                throw new Exception("Later results must update the tool line under an unchanged title.");

            cursor = new Point(90, 90);
            RaisePointer(window, Mouse.MouseLeaveEvent);
            window.SetShowBranding(true);
            window.UpdateLayout();
            if (footer.Visibility != Visibility.Hidden || title.Text != "rvt-mcp" || tool.Text != "Capture View"
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
            if (window.Opacity != 1 || window.HasAnimatedProperties || number.IsRolling)
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
