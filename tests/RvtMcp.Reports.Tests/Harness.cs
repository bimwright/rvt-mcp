using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Views;

/// <summary>One open Reports window over fakes; state saves go to memory, never to the user's config.</summary>
internal sealed class Scenario : IDisposable
{
    public Scenario(DisplayMode mode = DisplayMode.Wide, ReportsWindowState state = null, List<AgentReport> reports = null,
        ActiveDocumentIdentity active = null, bool noActive = false)
    {
        Runtime = new FakeReportsRuntime { Reports = reports ?? DemoData.Reports(), ActiveDocument = noActive ? null : active ?? DemoData.ActiveA };
        Navigation = new FakeNavigation();
        Screen = new FakeScreen();
        ViewModel = new ReportsViewModel(Runtime, Navigation, mode);
        Window = new ReportsWindow(ViewModel, state ?? new ReportsWindowState(mode == DisplayMode.Compact ? ReportsWindowMode.Compact : ReportsWindowMode.Wide),
            Saved.Add, Screen);
    }

    public FakeReportsRuntime Runtime { get; }
    public FakeNavigation Navigation { get; }
    public FakeScreen Screen { get; }
    public ReportsViewModel ViewModel { get; }
    public ReportsWindow Window { get; }
    public List<ReportsWindowState> Saved { get; } = new List<ReportsWindowState>();
    public ReportsWideView Wide => Window.View as ReportsWideView;
    public ReportsCompactView Compact => Window.View as ReportsCompactView;

    public Scenario Show()
    {
        Window.Show();
        WaitForRefresh();
        return this;
    }

    /// <summary>Waits for the view model's load, pumping the dispatcher so its Post callbacks run.</summary>
    public void WaitForRefresh()
    {
        var task = ViewModel.RefreshAsync();
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            H.Pump();
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("report load did not finish");
            Thread.Sleep(2);
        }
        task.GetAwaiter().GetResult();
        H.Pump();
    }

    public ReportListItemViewModel Item(string reportId) => ViewModel.Reports.FirstOrDefault(r => r.ReportId == reportId)
        ?? throw new Exception("FAIL: report " + reportId + " is not listed");

    public void Select(string reportId)
    {
        ViewModel.SelectedReport = Item(reportId);
        H.Pump();
    }

    public void Dispose()
    {
        Window.CloseForShutdown();
        ViewModel.Dispose();
        H.Pump();
    }
}

/// <summary>Shared harness helpers.</summary>
internal static class H
{
    public static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    public static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    /// <summary>Pumps until <paramref name="condition"/> holds, for work the OS finishes later (posted system commands).</summary>
    public static bool WaitFor(Func<bool> condition, int milliseconds = 2000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds) return false;
            Thread.Sleep(10);
            Pump();
        }
        Pump();
        return true;
    }

    public static bool Near(double a, double b) => Math.Abs(a - b) < 0.6;

    public static bool Bounds(Window w, double left, double top, double width, double height) =>
        Near(w.Left, left) && Near(w.Top, top) && Near(w.Width, width) && Near(w.Height, height);

    public static string Describe(Window w) => $"({w.Left:0.#}, {w.Top:0.#}, {w.Width:0.#}×{w.Height:0.#})";

    public static bool Same(ReportsWindowBounds b, double left, double top, double width, double height) =>
        b != null && Near(b.Left, left) && Near(b.Top, top) && Near(b.Width, width) && Near(b.Height, height);

    /// <summary>Real click path: the protected OnClick raises Click and runs the command, as a mouse click does.</summary>
    public static void Click(ButtonBase button)
    {
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, null);
        Pump();
    }

    public static void Click(Hyperlink link)
    {
        typeof(Hyperlink).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(link, null);
        Pump();
    }

    public static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is T match) yield return match;
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    public static IEnumerable<TextElement> Elements(FlowDocument document)
    {
        var pending = new Stack<object>();
        foreach (var block in document.Blocks.Reverse()) pending.Push(block);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is TextElement element) yield return element;
            IEnumerable<object> children = node switch
            {
                Paragraph p => p.Inlines,
                Span s => s.Inlines,
                List l => l.ListItems,
                ListItem li => li.Blocks,
                Table t => t.RowGroups,
                TableRowGroup g => g.Rows,
                TableRow r => r.Cells,
                TableCell c => c.Blocks,
                Section sec => sec.Blocks,
                _ => Enumerable.Empty<object>()
            };
            foreach (var child in children.Reverse()) pending.Push(child);
        }
    }

    public static string Text(TextElement element) =>
        new TextRange(element.ContentStart, element.ContentEnd).Text;

    public static ScrollViewer InnerScroll(DependencyObject root) => Visuals<ScrollViewer>(root).FirstOrDefault();

    public static int Handlers(object source, Type declaring, string eventName)
    {
        var field = declaring.GetField(eventName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
        var value = field.GetValue(field.IsStatic ? null : source) as Delegate;
        return value?.GetInvocationList().Length ?? 0;
    }

    public static int ViewModelHandlers(ReportsViewModel vm) => Handlers(vm, typeof(RvtMcp.Plugin.Presentation.ObservableObject), "PropertyChanged");
    public static int LanguageHandlers() => Handlers(null, typeof(L), "Changed");

    public static Color ColorOf(Brush brush) => (brush as SolidColorBrush)?.Color ?? Colors.Transparent;
    public static Color Hex(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Monitor work area of the window, in device pixels (real screen, for the maximize check).</summary>
    public static Rect WorkAreaPx(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(monitor, ref info)) throw new Exception("FAIL: no monitor info");
        return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
