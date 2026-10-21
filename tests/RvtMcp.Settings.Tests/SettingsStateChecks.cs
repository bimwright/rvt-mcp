using System;
using System.Collections.Generic;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Presentation.Settings;
using RvtMcp.Plugin.Views.Settings;

internal static class SettingsStateChecks
{
    internal static void Run()
    {
        using (var runtime = new Runtime())
        using (var model = new SettingsViewModel(runtime))
        {
            Expect(!model.IsDirty && runtime.Reads == 1 && runtime.Writes == 0, "load only reads");
            Expect(!model.ApplyCommand.CanExecute(null) && !model.DiscardCommand.CanExecute(null), "clean commands are unavailable");
            model.ApplyCommand.Execute(null);
            Expect(runtime.Writes == 0, "disabled command cannot force a save");
            var changes = 0;
            model.PropertyChanged += (s, e) => changes++;
            model.ToastIdleSeconds = 20;
            Expect(changes == 0, "equal value does not notify");
            model.ToastIdleSeconds = 30;
            model.CacheSendCodeBodies = true;
            Expect(model.IsDirty && runtime.Writes == 0, "editing stays staged");
            runtime.ApplyResult.Applied.Add("toastIdleSeconds");
            runtime.ApplyResult.Failed.Add("cache failed");
            runtime.ApplyResult.FieldErrors["cacheSendCodeBodies"] = "cache failed";
            Expect(model.ApplyCommand.CanExecute(null), "editing enables Apply");
            model.ApplyCommand.Execute(null);
            var report = model.LastApplyReport;
            Expect(!report.Succeeded && model.IsDirty && model.CacheSendCodeBodies, "partial save retains failed field");
            Expect(runtime.LastChanges.ToastIdleSeconds == 30 && runtime.LastChanges.CacheSendCodeBodies == true
                && runtime.LastChanges.PersistSendCodeBodies == null && runtime.LastChanges.WarningPolicy == null, "patch contains only edited fields");
            runtime.ApplyResult = new SettingsApplyReport();
            runtime.ApplyResult.Applied.Add("cacheSendCodeBodies");
            model.ApplyCommand.Execute(null);
            Expect(runtime.LastChanges.ToastIdleSeconds == null && runtime.LastChanges.CacheSendCodeBodies == true
                && !model.IsDirty && runtime.Writes == 2, "retry writes failed field once and clears dirty");
            model.ToastIdleSeconds = 60;
            model.ToastIdleSeconds = 30;
            Expect(!model.IsDirty, "successful save becomes the new baseline");
        }
        using (var runtime = new Runtime())
        using (var model = new SettingsViewModel(runtime))
        {
            model.ToastIdleSeconds = 60;
            model.SetToastEnabled(false);
            model.SetShowBranding(true);
            model.SetToastPosition(new ToastPositionOptions().WithOffset(12, 34));
            model.Cancel();
            Expect(!model.IsDirty && model.ToastIdleSeconds == 20 && !model.ToastEnabled && model.ShowBranding,
                "Discard reloads staged values and preserves immediate actions");
            Expect(runtime.ImmediateWrites == 3 && runtime.Writes == 0, "each immediate action executes once");
            runtime.SaveSucceeds = false;
            model.SetShowBranding(false);
            Expect(model.ImmediateWarningKey == "showBranding" && !string.IsNullOrEmpty(model.ImmediateWarning), "immediate failure is visible");
            runtime.SaveSucceeds = true;
            model.SetShowBranding(true);
            Expect(model.ImmediateWarning == null && !model.IsDirty, "immediate retry clears only immediate warning");
        }
        using (var runtime = new Runtime())
        using (var model = new SettingsViewModel(runtime))
        {
            runtime.IsWarningPolicyWriteDisabled = true;
            model.WarningPolicy = "accept-and-log";
            Expect(!model.IsDirty, "host override prevents staging");
            runtime.IsWarningPolicyWriteDisabled = false;
            model.WarningPolicy = "accept-and-log";
            runtime.IsWarningPolicyWriteDisabled = true;
            runtime.ApplyResult.Failed.Add("host override");
            runtime.ApplyResult.FieldErrors["warningPolicy"] = "host override";
            Expect(!model.Apply().Succeeded && !model.IsDirty && model.WarningPolicy == "rollback", "override appearing before Apply restores effective policy");
            runtime.ApplyResult = new SettingsApplyReport { JournalWasExpired = true };
            model.Apply();
            Expect(model.ImmediateWarningKey == "persistSendCodeBodies", "expiry is reported without auto-enabling journal");
            runtime.IsLanguageWriteDisabled = true;
            model.SetLanguage("fr");
            Expect(model.ImmediateWarningKey == "uiLanguage" && runtime.Language == "fr", "host language override retains session-only action");
        }
        var observedRuntime = new Runtime();
        var observed = new SettingsViewModel(observedRuntime);
        var notified = 0;
        var languages = 0;
        observed.PropertyChanged += (s, e) => notified++;
        observed.LanguageChanged += (s, e) => languages++;
        observedRuntime.RaiseLanguage();
        observedRuntime.RaiseLanguage();
        Expect(languages == 0, "duplicate language version is ignored");
        observedRuntime.LanguageVersion++;
        observedRuntime.RaiseLanguage();
        Expect(languages == 1, "new language version publishes once");
        var latePosition = observedRuntime.PositionCallback;
        var lateLanguage = observedRuntime.LanguageCallback;
        observed.Dispose();
        observed.Dispose();
        Expect(!observed.ApplyCommand.CanExecute(null) && !observed.CopyPortCommand.CanExecute(null), "disposed actions are unavailable");
        var afterDispose = notified;
        latePosition?.Invoke(false);
        lateLanguage?.Invoke(null, EventArgs.Empty);
        Expect(notified == afterDispose && observedRuntime.Subscribers == 0 && observedRuntime.Disposals == 1,
            "dispose detaches once and rejects already-queued callbacks");
        Console.WriteLine("PASS: real SettingsViewModel staging, partial apply/retry, immediate actions, overrides, expiry and lifetime without host/I/O");
    }

    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Runtime : ISettingsRuntime
    {
        public event EventHandler LanguageChanged;
        public event EventHandler ToastEnabledChanged;
        public event Action<bool> PositionSaved;
        public Action<bool> PositionCallback => PositionSaved;
        public EventHandler LanguageCallback => LanguageChanged;
        public int Subscribers => (LanguageChanged?.GetInvocationList().Length ?? 0)
            + (ToastEnabledChanged?.GetInvocationList().Length ?? 0) + (PositionSaved?.GetInvocationList().Length ?? 0);
        public int Reads, Writes, ImmediateWrites, Disposals;
        public bool SaveSucceeds = true;
        public string Language = "auto";
        public SettingsChanges LastChanges;
        public SettingsApplyReport ApplyResult = new SettingsApplyReport();
        public int LanguageVersion { get; set; }
        public string EffectiveLanguage => "en";
        public bool IsLanguageWriteDisabled { get; set; }
        public bool IsWarningPolicyWriteDisabled { get; set; }
        public bool ToastEnabled { get; private set; } = true;
        public bool ShowBranding;
        public ToastPositionOptions ToastPosition { get; private set; } = new ToastPositionOptions();
        public bool IsListenerRunning { get; private set; }
        public bool IsClientConnected => false;
        public string TransportKind => "TCP";
        public bool CanCopyPort => IsListenerRunning;
        public string PortText => "1234";
        public string EffectiveWarningPolicy => "rollback";
        public SettingsValues Read()
        {
            Reads++;
            return new SettingsValues { Snapshot = new SettingsSnapshot(), SelectedLanguage = Language,
                ToastEnabled = ToastEnabled, ShowBranding = ShowBranding, ToastPosition = ToastPosition,
                ToastIdleSeconds = 20, PersistSendCodeBodiesHours = 4, WarningPolicy = "rollback" };
        }
        public SettingsApplyReport Apply(SettingsChanges changes) { Writes++; LastChanges = changes; return ApplyResult; }
        public bool SetToastEnabled(bool enabled) { ImmediateWrites++; ToastEnabled = enabled; ToastEnabledChanged?.Invoke(this, EventArgs.Empty); return SaveSucceeds; }
        public bool SetShowBranding(bool show) { ImmediateWrites++; ShowBranding = show; return SaveSucceeds; }
        public bool SetToastPosition(ToastPositionOptions options) { ImmediateWrites++; ToastPosition = options; return SaveSucceeds; }
        public bool SetLanguage(string language) { Language = language; return !IsLanguageWriteDisabled && SaveSucceeds; }
        public bool StartListener() { IsListenerRunning = true; return true; }
        public void StopListener() { IsListenerRunning = false; }
        public void CopyPort() { }
        public void RaiseLanguage() => LanguageChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() { Disposals++; }
    }
}
