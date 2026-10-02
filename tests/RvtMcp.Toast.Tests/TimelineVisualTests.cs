using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views.Toast;

internal static class TimelineVisualTests
{
    internal static void Run(string captureDirectory = null)
    {
        var en = EmbeddedCatalog.Load(typeof(Program).Assembly, "en");
        L.InitializeForTests(StringTable.Build("en", en, null, null));
        var completed = new DateTimeOffset(2026, 10, 2, 14, 30, 0, TimeSpan.FromHours(7));
        var activity = new ActivityAggregator(now: () => TimeSpan.Zero, wallClock: () => completed);
        void Add(string title, string body, bool success = true)
        {
            activity.RecordResult(title, body, success, null, true, durationMs: 200);
            completed += TimeSpan.FromSeconds(2);
        }
        Add("Survey Change Impact", "Survey: coverage incomplete");
        var cursor = new Point(10, 10);
        var window = new McpToastWindow(activity.TakeRender().Card, null, null, null, null, null,
            () => cursor, () => false, "rvt-mcp 2027 · sample");
        try
        {
            window.SetShowBranding(false);
            window.CapturePointerBaseline(); window.SetPosition(50, 50); window.Show(); window.PlayEnterAnimation();
            Pump(40); Capture(window, captureDirectory, "compact.png");
            cursor = new Point(20, 10);
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Pump(250);
            var panel = Children<ToastActivityPanel>(window).Single();
            var list = Children<ListBox>(panel).Single();
            AssertRail(list, 0, true, true);
            Add("Set Element Parameters", "Parameter is read-only", false);
            window.Update(activity.TakeRender().Card); Pump(40);
            AssertRail(list, 0, true, false); // reused former-last item must gain its lower rail
            AssertRail(list, 1, false, true);
            var nodes = Children<Border>(list).Where(b => Equals(b.Tag, "OutcomeNode")).ToArray();
            if (nodes.Length != 2 || nodes[0].CornerRadius == nodes[1].CornerRadius
                || !nodes.Any(n => System.Windows.Automation.AutomationProperties.GetName(n) == "Failed"))
                throw new Exception("Timeline outcomes need distinct shapes and accessible status names.");
            Add("Get Element Parameters", "Items: 3");
            window.Update(activity.TakeRender().Card); Pump(40);
            AssertRail(list, 2, false, true);
            Capture(window, captureDirectory, "timeline-latest.png");
            for (var i = 0; i < 8; i++) Add("Query " + i, "Results: " + (i + 1));
            window.Update(activity.TakeRender().Card); Pump(40);
            var viewer = Children<ScrollViewer>(list).Single();
            viewer.ScrollToVerticalOffset(42); Pump(40);
            var oldOffset = viewer.VerticalOffset;
            Add("Incoming", "New result while reading older calls");
            window.Update(activity.TakeRender().Card); Pump(40);
            if (Math.Abs(viewer.VerticalOffset - oldOffset) > .1)
                throw new Exception("Timeline append moved the reader's older-call position.");
            Capture(window, captureDirectory, "timeline-older.png");
            var oldEntries = panel.Entries;
            var localized = new Dictionary<string, string>(en)
            {
                ["toast.activity.recent"] = "Hoạt động · {count:n} lệnh",
                ["toast.activity.success"] = "Thành công", ["toast.activity.failed"] = "Thất bại"
            };
            L.InitializeForTests(StringTable.Build("tt", localized, null, null));
            window.RefreshLocalization(); Pump(40);
            if (!ReferenceEquals(oldEntries, panel.Entries) || Math.Abs(viewer.VerticalOffset - oldOffset) > .1
                || !Children<TextBlock>(panel).Any(t => t.Text.StartsWith("Hoạt động"))
                || !Children<Border>(list).Any(b => Equals(b.Tag, "OutcomeNode")
                    && System.Windows.Automation.AutomationProperties.GetName(b) == "Thất bại"))
                throw new Exception("Localization must refresh chrome/status names without replacing history or scrolling.");
            Capture(window, captureDirectory, "timeline-localized.png");
            Console.WriteLine("PASS: real timeline endpoints after append, distinct shapes/status names, scroll retention, localization and captures");
        }
        finally
        {
            window.CloseImmediate();
            L.InitializeForTests(StringTable.Build("en", en, null, null));
        }
    }

    private static void AssertRail(ListBox list, int index, bool first, bool last)
    {
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        var borders = Children<Border>(container).ToArray();
        var top = borders.Single(b => Equals(b.Tag, "TimelineTopRail"));
        var bottom = borders.Single(b => Equals(b.Tag, "TimelineBottomRail"));
        if (top.Visibility != (first ? Visibility.Collapsed : Visibility.Visible)
            || bottom.Visibility != (last ? Visibility.Collapsed : Visibility.Visible))
            throw new Exception("Incorrect timeline rail endpoints at index " + index);
    }

    private static void Capture(Window window, string directory, string name)
    {
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right),
            (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, name))) encoder.Save(file);
        Console.WriteLine("CAPTURE: " + name + " " + bitmap.PixelWidth + "x" + bitmap.PixelHeight + " (synthetic sample)");
    }

    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var descendant in Children<T>(child)) yield return descendant;
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
