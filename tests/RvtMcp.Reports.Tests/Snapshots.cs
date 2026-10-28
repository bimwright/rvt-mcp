using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RvtMcp.Plugin.Reports.Presentation;
using static H;

/// <summary>--demo opens the window over sample data; --snapshot renders the five reference states at 150 %.</summary>
internal static class Snapshots
{
    /// <summary>Interactive preview; closing (hiding) the window ends it.</summary>
    public static int Demo()
    {
        using (var s = new Scenario(DisplayMode.Wide))
        {
            s.Navigation.Next = new ReportNavigationResult { Outcome = ReportNavigationOutcome.Selected, SelectedCount = 6, ViewName = "Level 2 - Mechanical" };
            var frame = new DispatcherFrame();
            s.Window.IsVisibleChanged += (sender, e) => { if (!s.Window.IsVisible) frame.Continue = false; };
            s.Show();
            Dispatcher.PushFrame(frame);
        }
        return 0;
    }

    public static int Save(string directory)
    {
        Directory.CreateDirectory(directory);
        Shoot(directory, "wide.png", new Scenario(DisplayMode.Wide), null);
        Shoot(directory, "wide-other-model.png", new Scenario(DisplayMode.Wide), s => { Click(s.Wide.AllFilterButton); s.Select("r03"); });
        Shoot(directory, "compact.png", new Scenario(DisplayMode.Compact), s => s.Select("r01"));
        Shoot(directory, "compact-expanded.png", new Scenario(DisplayMode.Compact), s =>
        {
            s.Select("r01");
            Click(s.Compact.OpenBody.Chip);
            s.Compact.OpenBody.ChangesView.BringIntoView(); // the opened list sits below the body
        });
        Shoot(directory, "empty.png", new Scenario(DisplayMode.Wide, noActive: true), null);
        Console.WriteLine("Saved 5 snapshots to " + directory);
        return 0;
    }

    private static void Shoot(string directory, string name, Scenario scenario, Action<Scenario> arrange)
    {
        using (scenario.Show())
        {
            arrange?.Invoke(scenario);
            Pump();
            var path = Path.Combine(directory, name);
            Render(scenario.Window, path);
            Console.WriteLine("  " + path);
        }
    }

    private static void Render(Window window, string path)
    {
        const double scale = 1.5;
        var content = (FrameworkElement)window.Content;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            // Absolute viewbox keeps layout margins; the default brush crops to rendered content.
            context.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
}
