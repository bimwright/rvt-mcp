using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Views;
using RvtMcp.Plugin.Views;
using System.Linq;
using Newtonsoft.Json.Linq;
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
        var days = new List<AgentReport> {
            DemoData.Report("day1", "completed", "Checked level 2 valves", DemoData.Local(0, 14, 32), DemoData.DocA()),
            DemoData.Report("day2", "completed", "Reviewed pipe tags", DemoData.Local(0, 13, 5), DemoData.DocA()),
            DemoData.Report("day3", "completed", "Reviewed yesterday's report", DemoData.Local(-1, 9, 10), DemoData.DocA()),
            DemoData.Report("day4", "completed", "Imported report with unknown date", "invalid", DemoData.DocA()) };
        days[2].UpdatedUtc = DemoData.Local(0, 14, 58);
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            Shoot(directory, "compact-days-" + (int)(scale * 100) + ".png", new Scenario(DisplayMode.Compact, reports: days), null, scale);
        Shoot(directory, "compact-new-reports.png", new Scenario(DisplayMode.Compact), s =>
        {
            s.Select(DemoData.LongReport);
            var scroll = InnerScroll(s.Compact.Feed);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 240);
            Pump();
            var reports = s.Runtime.Reports;
            reports.Insert(0, DemoData.Report("arrival", "completed", "New report", DemoData.Local(0, 14, 59), DemoData.DocA()));
            s.Runtime.Reports = reports;
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
        });
        Shoot(directory, "compact-long-pipe.png", new Scenario(DisplayMode.Compact), s => {
            s.Window.Width = s.Window.MinWidth;
            s.Runtime.SetHost(new ReportHostStatus(12345, true, "Pipe:synthetic-revit-" + new string('p', 100)));
        });
        Shoot(directory, "wide-tcp.png", new Scenario(DisplayMode.Wide), s => s.Runtime.SetHost(new ReportHostStatus(12345, true, "TCP:12390")));
        Shoot(directory, "compact-stopped.png", new Scenario(DisplayMode.Compact), s => s.Runtime.SetHost(new ReportHostStatus(12345, false, "Pipe:stale")));
        foreach (var mode in new[] { DisplayMode.Wide, DisplayMode.Compact })
            Shoot(directory, mode == DisplayMode.Wide ? "wide-title-hover.png" : "compact-title-hover.png", new Scenario(mode), s =>
            {
                s.Runtime.SetHost(new ReportHostStatus(12345, true, "TCP:12390"));
                ReportFinishChecks.Hover(s.Window, true);
            });
        foreach (var mode in new[] { DisplayMode.Wide, DisplayMode.Compact })
            Shoot(directory, mode == DisplayMode.Wide ? "wide-history.png" : "compact-history.png", new Scenario(mode), s =>
            {
                s.Runtime.HistoryReader = (id, c, e) => new JObject { ["status"] = "references", ["calls"] = new JArray(new JObject {
                    ["call_id"] = new string('a', 32), ["model_key"] = new string('b', 64), ["tool"] = "synthetic_set_parameter", ["status"] = "available",
                    ["complete"] = true, ["success"] = true, ["record_id"] = "synthetic-record", ["reason_status"] = "recorded",
                    ["reason"] = new JObject { ["reason"] = "Synthetic coordination request" },
                    ["elements"] = new JArray(new JObject { ["id"] = 101, ["kind"] = "modified", ["category"] = "Pipes",
                        ["before"] = new JObject { ["parameter"] = "Comments", ["value"] = "Before" },
                        ["after"] = new JObject { ["parameter"] = "Comments", ["value"] = "After" } }) }) };
                var report = s.Runtime.Reports.Single(r => r.ReportId == "r01");
                if (report.Changes != null) report.Changes.Scope = "calls";
                s.Select("r02"); s.Select("r01");
                var history = Visuals<ReportHistoryView>(s.Window).Single(); history.IsExpanded = true;
                Expect(WaitFor(() => Visuals<TextBlock>(history).Any(t => (t.Text ?? "").Contains("Synthetic coordination request"))), "History preview loaded");
                history.BringIntoView();
            });
        foreach (var asset in new[] { new KeyValuePair<string, DrawingImage>("reports", ReportAssets.Report), new KeyValuePair<string, DrawingImage>("settings", ReportAssets.Settings) })
            foreach (var pixels in new[] { 16, 24, 32, 64 })
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ReportAssets.Bitmap(asset.Value, pixels)));
                using (var stream = File.Create(Path.Combine(directory, "icon-" + asset.Key + "-" + pixels + ".png"))) encoder.Save(stream);
            }
        Console.WriteLine("Saved " + Directory.GetFiles(directory, "*.png").Length + " snapshots to " + directory);
        return 0;
    }

    private static void Shoot(string directory, string name, Scenario scenario, Action<Scenario> arrange, double scale = 1.5)
    {
        using (scenario.Show())
        {
            arrange?.Invoke(scenario);
            Pump();
            var path = Path.Combine(directory, name);
            Render(scenario.Window, path, scale);
            Console.WriteLine("  " + path);
        }
    }

    private static void Render(Window window, string path, double scale)
    {
        var content = (FrameworkElement)window.Content;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
}
