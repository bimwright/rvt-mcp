using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Views.Settings;

internal static class WarningPolicySettingsChecks
{
    internal static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rvt-warning-settings-" + Guid.NewGuid().ToString("N"));
        var previousPath = RvtMcpConfig.ConfigFilePathOverride;
        var previousEnv = Environment.GetEnvironmentVariable(RvtMcpConfig.EnvWarningPolicy);
        var previousJournal = Environment.GetEnvironmentVariable(RvtMcpConfig.EnvPersistSendCodeBodies);
        var previousTtl = Environment.GetEnvironmentVariable(RvtMcpConfig.EnvPersistSendCodeBodiesTtl);
        Directory.CreateDirectory(folder);
        try
        {
            Environment.SetEnvironmentVariable(RvtMcpConfig.EnvWarningPolicy, null);
            Environment.SetEnvironmentVariable(RvtMcpConfig.EnvPersistSendCodeBodies, null);
            Environment.SetEnvironmentVariable(RvtMcpConfig.EnvPersistSendCodeBodiesTtl, null);
            RvtMcpConfig.ConfigFilePathOverride = Path.Combine(folder, "config.json");
            File.WriteAllText(RvtMcpConfig.ConfigFilePathOverride, "{\"readOnly\":true}");
            var app = new App();
            var beforeCall = RvtMcpConfig.LoadReadOnly();
            WithWindow(app, (window, model) => {
                var policy = PolicyCombo(window);
                Expect((string)((ComboBoxItem)policy.SelectedItem).Tag == "rollback", "Settings defaults to rollback");
                policy.SelectedIndex = 1;
                Expect(model.IsDirty && model.WarningPolicy == "accept-and-log", "selecting opt-in is staged");
                Expect(RvtMcpConfig.LoadReadOnly().WarningPolicyOrDefault == "rollback", "a staged choice has no runtime effect");
                Click(window, "Discard changes");
                Expect(!model.IsDirty && (string)((ComboBoxItem)policy.SelectedItem).Tag == "rollback", "Discard restores the control and value");
                policy.SelectedIndex = 1;
                Click(window, "Apply");
                Expect(!model.IsDirty && RvtMcpConfig.LoadReadOnly().WarningPolicyOrDefault == "accept-and-log", "Apply persists the opt-in");
                Expect(RvtMcpConfig.LoadReadOnly().ReadOnlyOrDefault, "Apply preserves unrelated restrictions");
                Expect(beforeCall.WarningPolicyOrDefault == "rollback", "a captured call snapshot is unchanged");
            });
            WithWindow(app, (window, model) => {
                var policy = PolicyCombo(window);
                Expect((string)((ComboBoxItem)policy.SelectedItem).Tag == "accept-and-log", "reopening shows the saved opt-in");
                policy.SelectedIndex = 0;
                Click(window, "Apply");
                Expect(RvtMcpConfig.LoadReadOnly().WarningPolicyOrDefault == "rollback", "Apply can restore strict behavior for the next call");
            });
            WithWindow(app, (window, model) => {
                var before = File.ReadAllText(RvtMcpConfig.ConfigFilePathOverride);
                var branding = !model.ShowBranding;
                model.SetShowBranding(branding);
                Expect(app.Config.ShowBranding == branding && RvtMcpConfig.LoadReadOnly().ShowBrandingOrDefault == branding
                    && !model.IsDirty, "branding applies to the host and persists immediately without staged changes");
                File.WriteAllText(RvtMcpConfig.ConfigFilePathOverride, "{");
                model.SetShowBranding(!branding);
                Expect(model.ImmediateWarningKey == "showBranding" && model.ImmediateWarning != null
                    && File.ReadAllText(RvtMcpConfig.ConfigFilePathOverride) == "{", "branding failure is visible without overwriting invalid config");
                File.WriteAllText(RvtMcpConfig.ConfigFilePathOverride, before);
                model.SetShowBranding(branding);
                Expect(model.ImmediateWarning == null, "branding retry clears the save warning");
            });
            foreach (var hostPolicy in new[] { "accept-and-log", "invalid-mode", " " })
            {
                Environment.SetEnvironmentVariable(RvtMcpConfig.EnvWarningPolicy, hostPolicy);
                WithWindow(app, (window, model) => {
                    var expected = hostPolicy == "accept-and-log" ? "accept-and-log" : "rollback";
                    Expect(!PolicyCombo(window).IsEnabled && model.WarningPolicy == expected, "host environment override is visible and disables the dropdown, including malformed values");
                    model.WarningPolicy = expected == "rollback" ? "accept-and-log" : "rollback";
                    Expect(!model.IsDirty, "a host environment override cannot be bypassed through the presentation setter");
                });
            }
            Environment.SetEnvironmentVariable(RvtMcpConfig.EnvWarningPolicy, null);
            var original = File.ReadAllText(RvtMcpConfig.ConfigFilePathOverride);
            WithWindow(app, (window, model) => {
                var policy = PolicyCombo(window);
                policy.SelectedIndex = 1;
                // A malformed external edit must fail visibly, not turn into a new empty config.
                File.WriteAllText(RvtMcpConfig.ConfigFilePathOverride, "{");
                Click(window, "Apply");
                Expect(model.IsDirty && File.ReadAllText(RvtMcpConfig.ConfigFilePathOverride) == "{", "failed persistence keeps the choice staged and the file untouched");
                Expect(Children<TextBlock>(window).Any(text => text.Visibility == Visibility.Visible && text.Text.Contains("could not be saved")), "the actual row displays its save failure");
                File.WriteAllText(RvtMcpConfig.ConfigFilePathOverride, original);
                Click(window, "Apply");
                Expect(!model.IsDirty && RvtMcpConfig.LoadReadOnly().WarningPolicyOrDefault == "accept-and-log", "retry persists the same staged choice");
            });
            WithWindow(app, (window, model) => {
                var duration = Children<ComboBox>(window).Single(control => AutomationProperties.GetName(control) == "Journal duration (hours)");
                var presets = new[] { 1, 4, 8, 12, 24, 48, 168, 336 };
                var labels = new[] { "1h", "4h", "8h", "12h", "24h", "2d", "7d", "14d" };
                Expect(duration.Items.Cast<int>().SequenceEqual(presets), "journal has exactly the eight requested durations");
                for (var i = 0; i < presets.Length; i++)
                {
                    duration.SelectedItem = presets[i];
                    Pump();
                    Expect(Children<TextBlock>(duration).Any(text => text.IsVisible && text.Text == labels[i]), "rendered journal label: " + labels[i]);
                }
                var journal = Children<CheckBox>(window).Single(control => AutomationProperties.GetName(control) == "Keep send_code journal");
                journal.IsChecked = true;
                Expect(model.IsDirty && model.PersistSendCodeBodiesHours == 336, "14d remains staged without being clamped to 2d");
                Click(window, "Apply");
                var config = RvtMcpConfig.LoadReadOnly();
                Expect(config.PersistSendCodeBodiesHoursOrDefault == 336 && config.IsPersistSendCodeBodiesActive(), "the real writer retains 14d");
                var hours = (DateTimeOffset.Parse(config.PersistSendCodeBodiesUntil) - DateTimeOffset.UtcNow).TotalHours;
                Expect(hours > 335.9 && hours <= 336.01, "14d is the actual expiry window; hours=" + hours + "; until=" + config.PersistSendCodeBodiesUntil);
                Expect(Children<TextBlock>(duration).Any(text => text.IsVisible && text.Text == "14d"), "the actual combobox template displays days compactly");
                Expect(!Children<TextBlock>(window).Any(text => text.Text != null && text.Text.IndexOfAny(new[] { '\u2013', '\u2014', '\u2011', '\u2212' }) >= 0), "Settings has no long dash glyphs in its visible text");
            });
            Expect(RvtMcpConfig.TrySavePersistSendCodeBodiesHours(5, out _), "a legacy custom duration can still be saved");
            WithWindow(app, (window, model) => {
                var duration = Children<ComboBox>(window).Single(control => AutomationProperties.GetName(control) == "Journal duration (hours)");
                Expect(duration.Items.Count == 8 && duration.SelectedItem == null, "a legacy custom value adds no selectable preset");
                Expect(!model.IsDirty && model.PersistSendCodeBodiesHours == 5 && Children<TextBlock>(duration).Any(text => text.IsVisible && text.Text == "5h"), "reopening preserves and renders a legacy custom value");
            });
            var row = new ToolRow { Name = "fixture", Description = "before — after", TimeoutDisplay = "—", TimeoutHelpText = "1–2 s" };
            Expect(row.DescriptionDisplay == "before - after" && row.ToolTipText.EndsWith("before - after")
                && row.TimeoutDisplay == "-" && row.TimeoutHelpText == "1-2 s" && row.Description == "before — after", "Settings normalizes displayed dashes without changing source tool descriptions");
            Console.WriteLine("PASS: warning policy and journal real Settings UI/adapter, Apply, Discard, reopen, failure/retry, host override, rendered presets, legacy values and 14d retention");
        }
        finally
        {
            RvtMcpConfig.ConfigFilePathOverride = previousPath;
            Environment.SetEnvironmentVariable(RvtMcpConfig.EnvWarningPolicy, previousEnv);
            Environment.SetEnvironmentVariable(RvtMcpConfig.EnvPersistSendCodeBodies, previousJournal);
            Environment.SetEnvironmentVariable(RvtMcpConfig.EnvPersistSendCodeBodiesTtl, previousTtl);
            Directory.Delete(folder, true);
        }
    }

    private static void WithWindow(App app, Action<SettingsWindow, SettingsViewModel> check)
    {
        var model = new SettingsViewModel(new RvtMcp.Plugin.Infrastructure.UI.SettingsRuntime(app));
        var window = new SettingsWindow(model, new FakeTools());
        window.Show();
        try { Pump(); check(window, model); }
        finally { window.CloseForShutdown(); }
    }
    private static ComboBox PolicyCombo(SettingsWindow window) => Children<ComboBox>(window)
        .Single(control => AutomationProperties.GetName(control) == "Warning policy");
    private static void Click(SettingsWindow window, string text)
    {
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(Children<Button>(window).Single(button => Equals(button.Content, text)), null);
        Pump();
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in Children<T>(VisualTreeHelper.GetChild(root, i))) yield return value;
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
}
