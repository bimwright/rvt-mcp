using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Toast;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // A close callback can reflow a new toast before Show has assigned its
            // initial Top/Left. Exercise real WPF animation evaluation from NaN.
            var first = new McpToastWindow(Model(), null);
            try
            {
                first.AnimateToPosition(16, 24);
                first.Show();
                Pump();
                AssertPosition(first, 16, 24);
            }
            finally { first.CloseImmediate(); }
            Console.WriteLine("PASS: first placement from unset WPF coordinates");

            var manager = new McpToastManager(Dispatcher.CurrentDispatcher);
            var active = (List<McpToastWindow>)typeof(McpToastManager)
                .GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            try
            {
                manager.Complete(Model());
                var oldest = active[0];
                manager.Complete(Model());
                // Start a reflow animation, then immediately insert another toast.
                // A direct reflow must replace the previous animation's held value.
                oldest.CloseImmediate();
                manager.Complete(Model());
                Pump();
                AssertPosition(active[0], 16, 16);
                AssertPosition(active[1], 16 + active[0].ActualHeight + 8, 16);
                for (var i = 0; i < 6; i++) manager.Complete(Model());
                Pump();
                if (active.Count != 4) throw new Exception("Toast cap was not preserved.");
            }
            finally { manager.DismissAllImmediate(); }
            Console.WriteLine("PASS: overlapping animated/direct reflows and four-toast cap");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static McpToastViewModel Model() => new McpToastViewModel
    {
        Title = "Toast regression test", Summary = "Temporary test notification",
        Kind = ToolActivityKind.Read, Success = true
    };

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void AssertPosition(McpToastWindow window, double top, double left)
    {
        if (!double.IsFinite(window.Top) || !double.IsFinite(window.Left)
            // Native HWND positioning rounds to device pixels.
            || Math.Abs(window.Top - top) > 1 || Math.Abs(window.Left - left) > 1)
            throw new Exception($"Expected ({top}, {left}); got ({window.Top}, {window.Left}).");
    }
}
