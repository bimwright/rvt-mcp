using System;
using System.Windows.Controls;
using System.Windows.Threading;
using RvtMcp.Plugin.Presentation.Toast;
using RvtMcp.Plugin.Views.Toast;

internal static class ToastCardStateChecks
{
    internal static void Run()
    {
        var calls = 0; long clicked = 0, dismissed = 0;
        var aggregator = new ActivityAggregator(now: () => TimeSpan.Zero);
        aggregator.RecordResult("Read View", "password=private C:\\Users\\fixture\\model.rvt", true, null, true);
        var snapshot = aggregator.TakeRender().Card;
        var vm = new ToastCardViewModel(snapshot, "rvt-mcp 2027", id => dismissed = id, id => clicked = id);
        vm.PropertyChanged += (s, e) => calls++;
        Expect(vm.Succeeded == 1 && vm.Failed == 0 && vm.Title == "Read View" && vm.ShowCounters && vm.ThumbnailPath == null, "initial activity projection");
        Expect(!vm.RecentEntries[0].Body.Contains("private") && !vm.ToolTip.Contains("model.rvt"), "recent activity retains redaction");
        aggregator.RecordResult("Edit View", "Failed", false, null, true);
        vm.Apply(aggregator.TakeRender().Card);
        Expect(calls == 1 && vm.Succeeded == 1 && vm.Failed == 1 && vm.HasFailure && vm.RecentEntries.Count == 2, "coherent failure and counter update");
        var card = vm.CardId;
        vm.Apply(new ActivitySnapshot(card + 1, false, 90, 0, 0, "wrong card", "", true, false));
        Expect(calls == 1 && vm.Title == "Edit View", "old slot cannot accept replacement identity");
        vm.ClickCommand.Execute(null); vm.DismissCommand.Execute(null);
        Expect(clicked == card && dismissed == card, "commands preserve immutable card attribution");
        vm.Dispose(); vm.Dispose(); vm.Apply(snapshot); vm.RefreshLocalization();
        clicked = dismissed = 0; vm.ClickCommand.Execute(null); vm.DismissCommand.Execute(null);
        Expect(calls == 1 && clicked == 0 && dismissed == 0 && !vm.ClickCommand.CanExecute(null), "disposed card rejects callbacks and late snapshots");

        var translated = "connected";
        var fail = false;
        var resolutions = 0;
        var status = new ActivitySnapshot(91, true, 0, 0, 0, "captured", "fallback", true, false,
            () => { resolutions++; if (fail) throw new Exception("fixture localization"); return new ActivityStatusText(translated, "body " + translated); });
        using (var state = new ToastCardViewModel(status, "rvt-mcp 2022", null, null))
        {
            Expect(state.IsStatus && !state.ShowCounters && state.Title == "connected" && resolutions == 1, "status resolves once on creation");
            translated = "translated"; state.RefreshLocalization();
            Expect(state.Title == translated && state.Body == "body translated" && state.CardId == 91, "localization does not replace card");
            fail = true; state.RefreshLocalization();
            Expect(state.Title == "captured" && state.Body == "fallback", "failed resolver preserves captured fallback");
        }

        using var bindings = new WpfBindingErrors();
        var closed = 0;
        var window = new McpToastWindow(snapshot, (w, id) => closed++, null, id => clicked = id, null, null, motionEnabled: () => false);
        var bound = (ToastCardViewModel)window.DataContext;
        try
        {
            window.Show(); window.PlayEnterAnimation(); Pump();
            var tool = Field<TextBlock>(window, "_toolText");
            Expect(tool.Text == bound.Title, "real card text is bound to presentation");
            aggregator.RecordResult("Latest View", "Done", true, null, true);
            window.Update(aggregator.TakeRender().Card); Pump();
            Expect(ReferenceEquals(bound, window.DataContext) && tool.Text == "Latest View" && window.Opacity == 1, "same VM/window updates data without replaying enter");
        }
        finally { window.CloseImmediate(); Pump(); }
        window.Update(snapshot); window.RefreshLocalization(); bound.ClickCommand.Execute(null);
        Expect(closed == 1 && window.DataContext == null && !bound.ClickCommand.CanExecute(null), "close clears bindings and deactivates commands");
        bindings.AssertClean();
        Console.WriteLine("PASS: Toast card projection, privacy, identity, commands, localization, real bindings and closed-card cleanup");
    }
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(source);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
}
