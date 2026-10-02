using System;
using System.Windows;
using System.Windows.Input;
using RvtMcp.Plugin.Views.Toast;

internal static class HoverRegressionTests
{
    internal static void Run()
    {
        var now = TimeSpan.Zero;
        var activity = new ActivityAggregator(() => 2, () => now);
        activity.RecordResult("Query", "Done", true, null, true);
        var snapshot = activity.TakeRender().Card;
        var cursor = new Point(100, 200);
        var entered = 0;
        var window = new McpToastWindow(snapshot, null, null, null,
            id => { entered++; activity.PointerEntered(id); }, activity.PointerLeft,
            () => cursor, () => false);
        try
        {
            window.CapturePointerBaseline();
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.MouseEnterEvent });
            if (entered != 0) throw new Exception("Stationary entry must not pause idle.");
            now = TimeSpan.FromSeconds(1);
            cursor = new Point(101, 200);
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.MouseMoveEvent });
            now = TimeSpan.FromSeconds(3);
            activity.Tick(true);
            if (entered == 0 || activity.TakeRender().Phase != ActivityCardPhase.Visible)
                throw new Exception("First real move under an existing card must pause idle; card expired while reading.");
        }
        finally { window.CloseImmediate(); }
        Console.WriteLine("PASS: first real movement after stationary entry pauses idle through the actual WPF event path");
    }
}
