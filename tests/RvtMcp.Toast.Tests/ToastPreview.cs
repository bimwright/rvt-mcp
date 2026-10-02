using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Toast;

/// <summary>Interactive production-window preview with simulated results, never Revit tool calls.</summary>
internal sealed class ToastPreview : Window
{
    private readonly ActivityAggregator _activity = new ActivityAggregator(() => 60);
    private readonly McpToastManager _manager;
    private readonly DispatcherTimer _sequence = new DispatcherTimer();
    private readonly DispatcherTimer _start = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
    private readonly TextBlock _state = new TextBlock { Margin = new Thickness(0, 10, 0, 10), TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _log = new ListBox { Height = 220 };
    private int _index;
    private int _captureIndex;
    private int _identityIndex;
    private readonly string[] _identities =
    {
        "rvt-mcp 2027",
        "rvt-mcp 2022",
        null
    };
    private readonly (string Name, bool Capture)[] _commands =
    {
        ("Get Current View Info", false), ("List Levels", false), ("List Rooms", false),
        ("Get Element Info", false), ("Capture View", true), ("Set Parameter", false),
        ("Create Wall", false), ("Move Elements", false), ("Capture View", true), ("List Elements", false)
    };

    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new ToastPreview());
    }

    private ToastPreview()
    {
        _manager = new McpToastManager(Dispatcher, _activity,
            onClick: _ => Log("Card click → History callback (preview only); card dismissed."),
            instanceIdentity: () => _identities[_identityIndex],
            motionEnabled: () => true);
        Title = "RVT-MCP — production toast preview (simulated results)";
        Width = 620; Height = 535; Left = 450; Top = 130;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = McpToastTheme.UiFont;
        FontSize = 13;
        Background = McpToastTheme.Background;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Production toast · activity timeline", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Real window + manager + aggregator, simulated results. No Revit/API calls.\nMotion is forced on in this preview. Hover to read the latest three calls; scroll up for older calls. New results follow the bottom and preserve older reading positions. ×: dismiss only.",
            TextWrapping = TextWrapping.Wrap, Foreground = McpToastTheme.TextSecondary, Margin = new Thickness(0, 8, 0, 12)
        });
        var actions = new WrapPanel();
        AddButton(actions, "Replay 10", () => StartSequence(1100));
        AddButton(actions, "Burst 10", () => StartSequence(90));
        AddButton(actions, "+1 error", () => { StopSequence(); Deliver("Set Parameter", false, false); });
        AddButton(actions, "Status card", () =>
        {
            Reset();
            _activity.ShowStatus("Agent connected", "rvt-mcp is ready (simulated)", 60);
            _manager.Render(); PositionCard();
        });
        AddButton(actions, "Instance", () =>
        {
            _identityIndex = (_identityIndex + 1) % _identities.Length;
            var label = _identities[_identityIndex] ?? "(none)";
            Log("Instance identity → " + label);
            _activity.ShowStatus("Identity preview", label, 60);
            _manager.Render(); PositionCard();
        });
        AddButton(actions, "Reset", Reset);
        panel.Children.Add(actions);
        panel.Children.Add(_state);
        panel.Children.Add(_log);
        panel.Children.Add(new TextBlock
        {
            Text = "10 Success · 0 Failed · 2 Capture after replay (captures also count as success). The two captures differ in shape (wide, then tall) to show centring and the cross-fade.\nIdle timeout: 60 s; hover pauses it. Closing this preview closes the toast too.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = McpToastTheme.TextSecondary, Margin = new Thickness(0, 12, 0, 0)
        });
        Content = panel;
        _sequence.Tick += (_, __) =>
        {
            var command = _commands[_index++];
            Deliver(command.Name, true, command.Capture);
            if (_index == _commands.Length) _sequence.Stop();
        };
        _start.Tick += (_, __) => { _start.Stop(); StartSequence(1100); };
        Loaded += (_, __) =>
        {
            _activity.ShowStatus("Toast preview", "10 simulated results start in 3 seconds.", 60);
            _manager.Render(); PositionCard(); _start.Start();
        };
        Closed += (_, __) => { StopSequence(); _manager.DismissAllImmediate(); };
    }

    private static void AddButton(Panel panel, string title, Action action)
    {
        var button = new Button { Content = title, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8) };
        button.Click += (_, __) => action();
        panel.Children.Add(button);
    }

    private void StartSequence(int interval)
    {
        Reset();
        _sequence.Interval = TimeSpan.FromMilliseconds(interval);
        _sequence.Start();
    }

    private void StopSequence() { _sequence.Stop(); _start.Stop(); }

    private void Reset()
    {
        StopSequence();
        _manager.DismissAllImmediate();
        _activity.Reset();
        _index = 0;
        _captureIndex = 0;
        _log.Items.Clear();
        _state.Text = "Ready";
    }

    private void Deliver(string name, bool success, bool capture)
    {
        _activity.RecordResult(name, success ? "Simulated success" : "Parameter is read-only (simulated)",
            success, capture ? PreviewPng(_captureIndex++ % 2) : null, true);
        _manager.Render();
        PositionCard();
        Log($"MOCK {name} → {(success ? "SUCCESS" : "ERROR")}{(capture ? " + CAPTURE" : "")}");
        _state.Text = $"Latest: {name} · replay {_index}/10";
    }

    /// <summary>A real PNG under %TEMP% so the capture rows in the replay exercise the
    /// thumbnail row end to end (the path allowlist covers temp and captures dirs).</summary>
    private static string PreviewPng(int variant)
    {
        var wide = variant == 0;
        var path = Path.Combine(Path.GetTempPath(), wide ? "rvtmcp-toast-preview-wide.png" : "rvtmcp-toast-preview-tall.png");
        if (File.Exists(path))
            return path;

        var width = wide ? 500 : 260;
        var height = wide ? 240 : 380;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(wide ? Color.FromRgb(0x20, 0x4A, 0x87) : Color.FromRgb(0x87, 0x4A, 0x20)),
                null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), null, new Rect(30, height - 100, 200, 70));
            dc.DrawText(new FormattedText(wide ? "captured view (wide)" : "captured view (tall)", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 22, System.Windows.Media.Brushes.White, 1.25),
                new Point(30, 40));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        return path;
    }

    private static void PositionCard()
    {
        foreach (var window in Application.Current.Windows.OfType<McpToastWindow>())
            window.SetPosition(170, 64);
    }

    private void Log(string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
        _log.Items.Add(line);
        _log.ScrollIntoView(line);
        Console.WriteLine(line);
    }
}
