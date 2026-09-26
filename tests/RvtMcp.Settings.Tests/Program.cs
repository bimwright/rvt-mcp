using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RvtMcp.Plugin.Views.Settings;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--demo")
                return Demo();
            if (args.Length == 2 && args[0] == "--snapshot")
                return Snapshot(args[1]);
            CheckTabs();
            CheckStagedFooter();
            CheckInlineApplyError();
            CheckImmediateToast();
            CheckConnection();
            CheckToolsGrid();
            CheckLifecycle();
            Console.WriteLine("PASS: Settings window layout, staging, errors and lifecycle");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckTabs()
    {
        Run((window, settings, tools) =>
        {
            var tabs = Field<TabControl>(window, "_tabs");
            var headers = tabs.Items.Cast<TabItem>().Select(item => (string)item.Header).ToArray();
            Expect(string.Join(",", headers) == "General,Toast,Tools,About", "tab order/captions: " + string.Join(",", headers));
            settings.RaiseLanguageChanged();
            Expect(tabs.SelectedIndex == 0, "language change keeps the selected tab");
            Expect((string)((TabItem)tabs.Items[2]).Header == "Tools", "Tools caption stays invariant");
        });
        Console.WriteLine("PASS: four tabs in order; Tools caption invariant across relocalization");
    }

    private static void CheckStagedFooter()
    {
        Run((window, settings, tools) =>
        {
            var apply = Field<Button>(window, "_apply");
            var discard = Field<Button>(window, "_discard");
            var footer = Field<TextBlock>(window, "_footerText");
            var idle = Field<ComboBox>(window, "_toastIdle");
            Expect(!apply.IsEnabled && !discard.IsEnabled && footer.Text == "", "clean state: Apply/Discard disabled, no footer text");

            idle.SelectedItem = 30;
            Expect(settings.ToastIdleSeconds == 30 && settings.IsDirty, "idle choice is staged");
            Expect(apply.IsEnabled && discard.IsEnabled && footer.Text == "Unsaved changes", "dirty state enables Apply/Discard and says so");

            Click(discard);
            Expect(settings.ToastIdleSeconds == 20 && (int)idle.SelectedItem == 20, "Discard reverts the staged value and the control");
            Expect(!apply.IsEnabled && footer.Text == "", "Discard returns to the clean state");

            idle.SelectedItem = 60;
            Click(apply);
            Expect(settings.SavedIdle == 60 && !settings.IsDirty, "Apply saves the staged value");
            Expect(!apply.IsEnabled && footer.Text == "Applied", "Apply confirms in the footer");
        });
        Console.WriteLine("PASS: Apply/Discard enable only with staged changes; Discard reverts controls");
    }

    private static void CheckInlineApplyError()
    {
        Run((window, settings, tools) =>
        {
            settings.FailApplyKey = "cacheSendCodeBodies";
            Field<CheckBox>(window, "_cacheSendCodeBodies").IsChecked = true;
            Click(Field<Button>(window, "_apply"));
            var errors = Field<System.Collections.Generic.Dictionary<string, TextBlock>>(window, "_fieldErrors");
            var error = errors["cacheSendCodeBodies"];
            Expect(error.Visibility == Visibility.Visible && error.Text.Contains("could not be saved"), "failed key shows under its row");
            Expect(errors.Where(pair => pair.Key != "cacheSendCodeBodies").All(pair => pair.Value.Visibility == Visibility.Collapsed), "other rows stay clean");
            Expect(Field<Button>(window, "_apply").IsEnabled && Field<TextBlock>(window, "_footerText").Text == "Unsaved changes", "failed change stays staged");

            settings.FailApplyKey = null;
            Click(Field<Button>(window, "_apply"));
            Expect(error.Visibility == Visibility.Collapsed, "successful retry clears the inline error");

            settings.SetImmediateWarning("enableToast", "enableToast: could not be saved");
            Expect(errors["enableToast"].Visibility == Visibility.Visible, "immediate save warning renders under the toast switch");
            settings.SetImmediateWarning("unknownKey", "Something else failed");
            Expect(Field<TextBlock>(window, "_footerText").Text == "Something else failed", "warning without a row falls back to the footer");
        });
        Console.WriteLine("PASS: save errors render inline under the related setting");
    }

    private static void CheckImmediateToast()
    {
        Run((window, settings, tools) =>
        {
            var toggle = Field<CheckBox>(window, "_toastEnabled");
            var idle = Field<ComboBox>(window, "_toastIdle");
            toggle.IsChecked = false;
            Expect(!settings.ToastEnabled && !settings.IsDirty, "toast switch applies immediately, not staged");
            Expect(!idle.IsEnabled && (int)idle.SelectedItem == 20, "idle duration is disabled but kept while toast is off");
            Expect(System.Windows.Automation.AutomationProperties.GetName(toggle) == "Show activity notifications", "switch is named by its row label");
        });
        Console.WriteLine("PASS: toast switch is immediate; idle duration disabled while off");
    }

    private static void CheckConnection()
    {
        Run((window, settings, tools) =>
        {
            var pill = Field<Border>(window, "_statePill");
            var copy = Field<Button>(window, "_copyPort");
            Expect(pill.Background == SettingsStyles.SuccessFill && copy.IsEnabled, "connected TCP: green state, Copy enabled");
            Click(copy);
            Expect(settings.CopyCount == 1 && (string)copy.Content == "Copied", "Copy copies once and confirms on the button");

            settings.IsClientConnected = false;
            settings.RaiseConnectionChanged();
            Expect(pill.Background == SettingsStyles.WarningFill, "listener without client: amber waiting state");

            settings.TransportKind = "Named Pipe";
            settings.IsListenerRunning = false;
            settings.RaiseConnectionChanged();
            Expect(pill.Background == SettingsStyles.NeutralFill, "stopped listener: neutral state");
            Expect(!copy.IsEnabled && (string)copy.ToolTip == "Port is not available", "no port: Copy disabled with an explanation");
            Expect(Plain(Field<TextBlock>(window, "_transportText")) == "Transport type: Named Pipe   |   Port: Not applicable", "no port: transport line says Not applicable");
        });
        Console.WriteLine("PASS: connection state pill and port Copy follow the transport");

        Run((window, settings, tools) =>
        {
            var line = Field<TextBlock>(window, "_transportText");
            var listener = Field<CheckBox>(window, "_listenerEnabled");
            var restart = Field<Button>(window, "_restartListener");
            Expect(Plain(line) == "Transport type: TCP   |   Port: 49891" && listener.IsChecked == true && restart.IsEnabled, "running TCP listener: one transport line, switch on");
            Click(restart);
            Expect(settings.Port == 49892 && Plain(line).EndsWith("Port: 49892"), "restart shows the new port");
            listener.IsChecked = false;
            Expect(!settings.IsListenerRunning && !restart.IsEnabled && !Field<Button>(window, "_copyPort").IsEnabled, "switch off stops the listener; restart and Copy disabled");
            Expect(Field<Border>(window, "_statePill").Background == SettingsStyles.NeutralFill, "stopped listener shows the neutral state");
            listener.IsChecked = true;
            Expect(settings.IsListenerRunning && restart.IsEnabled, "switch on starts the listener again");
        });
        Console.WriteLine("PASS: listener On/Off and restart from the State row");
    }

    private static void CheckToolsGrid()
    {
        Run((window, settings, tools) =>
        {
            var grid = Field<DataGrid>(window, "_toolsGrid");
            Expect(grid.Columns.Count == 5 && grid.IsReadOnly && !grid.CanUserAddRows, "five read-only columns");
            var headers = string.Join(",", grid.Columns.Select(column => (string)column.Header));
            Expect(headers == "No.,Tool Name,Description,Source,Time-out", "grid headers: " + headers);
            var before = tools.RefreshCount;
            window.SelectTab(SettingsTab.Tools);
            Expect(tools.RefreshCount == before + 1, "opening Tools refreshes the catalog");
            window.SelectTab(SettingsTab.General);
            Field<ComboBox>(window, "_journalHours").SelectedItem = 8;
            Expect(tools.RefreshCount == before + 1, "combo selection inside a tab is not a tab switch");
            Click(Field<Button>(window, "_discard"));
        });
        Console.WriteLine("PASS: Tools grid read-only with five columns; refresh only on tab switch");
    }

    private static void CheckLifecycle()
    {
        var settings = new FakeSettings();
        var tools = new FakeTools();
        for (var i = 0; i < 3; i++)
        {
            settings.Disposed = false;
            var window = new SettingsWindow(settings, tools);
            window.Show();
            Expect(settings.PropertyChangedSubscribers == 1 && settings.LanguageChangedSubscribers == 1, "one handler per open window");
            window.Close();
            Expect(settings.PropertyChangedSubscribers == 0 && settings.LanguageChangedSubscribers == 0 && settings.Disposed && tools.Disposed,
                "close detaches handlers and disposes the presentations");
        }
        Console.WriteLine("PASS: open/close three times leaves no handlers behind");
    }

    private static int Snapshot(string directory)
    {
        Directory.CreateDirectory(directory);
        var settings = new FakeSettings();
        var window = new SettingsWindow(settings, new FakeTools()) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.Show();
        foreach (var tab in new[] { SettingsTab.General, SettingsTab.Toast, SettingsTab.Tools, SettingsTab.About })
        {
            window.SelectTab(tab);
            if (tab == SettingsTab.Toast) Field<ComboBox>(window, "_toastIdle").SelectedItem = 30;
            Pump();
            Save(window, Path.Combine(directory, tab.ToString().ToLowerInvariant() + ".png"));
        }
        var license = new LicenseWindow(AboutInfoProvider.Create(Assembly.GetExecutingAssembly(), "2024"), window);
        license.Show();
        Pump();
        Save(license, Path.Combine(directory, "license.png"));
        license.Close();
        window.CloseForShutdown();
        Console.WriteLine("Saved General/Toast/Tools/About/License snapshots to " + directory);
        return 0;
    }

    private static int Demo()
    {
        var window = new SettingsWindow(new FakeSettings(), new FakeTools()) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.Title += " (preview)";
        new Application { ShutdownMode = ShutdownMode.OnMainWindowClose }.Run(window);
        return 0;
    }

    private static void Save(Window window, string path)
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

    private static void Run(Action<SettingsWindow, FakeSettings, FakeTools> check)
    {
        var settings = new FakeSettings();
        var tools = new FakeTools();
        var window = new SettingsWindow(settings, tools);
        window.Show();
        try
        {
            Pump();
            check(window, settings, tools);
        }
        finally
        {
            window.CloseForShutdown();
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    // TextBlock.Text does not track inlines added before the window is shown.
    private static string Plain(TextBlock text) => string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
