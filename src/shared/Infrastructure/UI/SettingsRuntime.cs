using System;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation.Settings;
using RvtMcp.Plugin.Views.Settings;

namespace RvtMcp.Plugin.Infrastructure.UI
{
    /// <summary>Existing configuration writer and host actions, observed on this window's dispatcher.</summary>
    public sealed class SettingsRuntime : ISettingsRuntime
    {
        private readonly App _app;
        private readonly Dispatcher _dispatcher;
        private readonly Action _detachPosition;
        private RvtMcpConfig _snapshot;
        private string _transportKind;
        private bool _disposed;

        public SettingsRuntime(App app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _dispatcher = Dispatcher.CurrentDispatcher;
            L.Changed += OnLanguageChanged;
            _app.ToastEnabledChanged += OnToastEnabledChanged;
            // Capture the subscribed instance: a later host replacement must not retain this adapter.
            var notifier = _app.ToastNotifier;
            if (notifier != null)
            {
                notifier.PositionSaved += OnPositionSaved;
                _detachPosition = () => notifier.PositionSaved -= OnPositionSaved;
            }
        }

        public event EventHandler LanguageChanged;
        public event EventHandler ToastEnabledChanged;
        public event Action<bool> PositionSaved;
        public int LanguageVersion => L.Version;
        public string EffectiveLanguage => L.Locale;
        public bool IsLanguageWriteDisabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RvtMcpConfig.EnvUiLanguage));
        public bool IsWarningPolicyWriteDisabled => Environment.GetEnvironmentVariable(RvtMcpConfig.EnvWarningPolicy) != null;
        public bool ToastEnabled => _app.ToastEnabled;
        public ToastPositionOptions ToastPosition => _app.ToastNotifier?.PositionOptions;
        public bool IsListenerRunning => _app.IsTransportRunning && _app.Transport != null;
        public bool IsClientConnected => IsListenerRunning && _app.Transport.IsClientConnected;
        public bool CanCopyPort => _app.Transport is TcpTransportServer && _app.IsTransportRunning;
        public string PortText => CanCopyPort ? ((TcpTransportServer)_app.Transport).Port.ToString()
            : SettingsText.Text("settings.general.notApplicable", "Not applicable");
        public string EffectiveWarningPolicy => _snapshot.WarningPolicyOrDefault;
        public string TransportKind
        {
            get
            {
                var info = _app.Transport?.ConnectionInfo ?? string.Empty;
                if (info.StartsWith("TCP:", StringComparison.OrdinalIgnoreCase)) _transportKind = "TCP";
                else if (info.StartsWith("Pipe:", StringComparison.OrdinalIgnoreCase)) _transportKind = "Named Pipe";
                return _transportKind ?? "-";
            }
        }

        public SettingsValues Read()
        {
            _snapshot = RvtMcpConfig.LoadReadOnly();
            var language = LocaleResolver.NormalizeCode(_snapshot.UiLanguage);
            return new SettingsValues
            {
                Snapshot = new SettingsSnapshot
                {
                    LocaleRequested = language, LocaleEffective = L.Locale,
                    LocaleSource = IsLanguageWriteDisabled ? InvariantLanguageText.SourceEnvironmentOverride
                        : (_snapshot.UiLanguage == null ? InvariantLanguageText.AutoOption : InvariantLanguageText.SourceSavedSetting),
                    RevitYear = AuthToken.RevitVersion ?? "-", TransportKind = TransportKind,
                    ReadAtUtc = DateTimeOffset.UtcNow,
                },
                SelectedLanguage = language, ToastEnabled = _app.ToastEnabled,
                ShowBranding = _app.ToastNotifier?.ShowBranding ?? _snapshot.ShowBrandingOrDefault,
                ToastPosition = ToastPosition ?? _snapshot.ToastPositionOrDefault,
                ToastIdleSeconds = _snapshot.ToastIdleSecondsOrDefault,
                PersistSendCodeBodies = _snapshot.IsPersistSendCodeBodiesActive(),
                PersistSendCodeBodiesHours = _snapshot.PersistSendCodeBodiesHoursOrDefault,
                CacheSendCodeBodies = _snapshot.CacheSendCodeBodiesOrDefault,
                WarningPolicy = _snapshot.WarningPolicyOrDefault,
            };
        }

        public SettingsApplyReport Apply(SettingsChanges changes)
        {
            var expired = false;
            if (_snapshot != null && _snapshot.IsPersistSendCodeBodiesActive())
            {
                try { expired = !RvtMcpConfig.LoadReadOnly().IsPersistSendCodeBodiesActive(); } catch { }
            }
            var blocked = changes.WarningPolicy != null && IsWarningPolicyWriteDisabled;
            var applied = RvtMcpConfig.TryApplySettings(new RvtMcpConfig.SettingsPatch
            {
                ToastIdleSeconds = changes.ToastIdleSeconds,
                PersistSendCodeBodies = changes.PersistSendCodeBodies,
                PersistSendCodeBodiesHours = changes.PersistSendCodeBodiesHours,
                CacheSendCodeBodies = changes.CacheSendCodeBodies,
                WarningPolicy = blocked ? null : changes.WarningPolicy,
            });
            var report = new SettingsApplyReport { JournalWasExpired = expired || applied.JournalWasExpired };
            foreach (var key in applied.Applied) report.Applied.Add(key);
            foreach (var failure in applied.Failed)
            {
                var message = SaveFailed(failure.Key);
                report.Failed.Add(message);
                report.FieldErrors[failure.Key] = message;
                App.DebugLog("Settings apply failed for " + failure.Key + ": " + failure.Error);
            }
            if (blocked)
            {
                var message = SettingsText.Text("security.warningPolicy.environmentOverride", "BIMWRIGHT_WARNING_POLICY overrides the saved setting in this Revit process.");
                report.Failed.Add(message);
                report.FieldErrors["warningPolicy"] = message;
            }
            RefreshEffectiveConfig();
            if (_app.Config != null)
            {
                if (applied.Applied.Contains("toastIdleSeconds")) _app.Config.ToastIdleSeconds = changes.ToastIdleSeconds;
                if (applied.Applied.Contains("persistSendCodeBodiesHours")) _app.Config.PersistSendCodeBodiesHours = changes.PersistSendCodeBodiesHours;
                if (applied.Applied.Contains("persistSendCodeBodies")) _app.Config.PersistSendCodeBodies = changes.PersistSendCodeBodies;
                if (applied.Applied.Contains("cacheSendCodeBodies")) _app.Config.CacheSendCodeBodies = changes.CacheSendCodeBodies;
            }
            return report;
        }

        private void RefreshEffectiveConfig()
        {
            try
            {
                var persisted = RvtMcpConfig.LoadReadOnly();
                _snapshot = persisted;
                if (_app.Config == null) return;
                _app.Config.ToastIdleSeconds = persisted.ToastIdleSeconds;
                _app.Config.PersistSendCodeBodiesHours = persisted.PersistSendCodeBodiesHours;
                _app.Config.PersistSendCodeBodies = persisted.PersistSendCodeBodies;
                _app.Config.PersistSendCodeBodiesUntil = persisted.PersistSendCodeBodiesUntil;
                _app.Config.PersistSendCodeBodiesRequiresExplicitEnable = persisted.PersistSendCodeBodiesRequiresExplicitEnable;
                _app.Config.CacheSendCodeBodies = persisted.CacheSendCodeBodies;
                _app.Config.WarningPolicy = persisted.WarningPolicy?.DeepClone();
            }
            catch { /* The writer already reported failure; refresh must not relabel a successful save. */ }
        }

        public bool SetToastEnabled(bool enabled)
        {
            _app.ToastEnabled = enabled;
            if (_app.Config != null) _app.Config.EnableToast = enabled;
            string error;
            var saved = RvtMcpConfig.TrySaveEnableToast(enabled, out error);
            if (!saved) App.DebugLog("Settings save enableToast failed: " + error);
            _app.ToastNotifier?.OnToastEnabledChanged(enabled, saved);
            return saved;
        }

        public bool SetShowBranding(bool show)
        {
            if (_app.Config != null) _app.Config.ShowBranding = show;
            _app.ToastNotifier?.SetShowBranding(show);
            string error;
            var saved = RvtMcpConfig.TrySaveShowBranding(show, out error);
            if (!saved) App.DebugLog("Settings save showBranding failed: " + error);
            return saved;
        }

        public bool SetToastPosition(ToastPositionOptions options)
        {
            _app.ToastNotifier?.SetPositionOptions(options);
            string error;
            var saved = RvtMcpConfig.TrySaveToastPosition(options, out error);
            if (!saved) App.DebugLog("Settings save toast position failed: " + error);
            return saved;
        }

        public bool SetLanguage(string language)
        {
            var saved = false;
            if (!IsLanguageWriteDisabled)
            {
                string error;
                saved = RvtMcpConfig.TrySaveUiLanguage(language, out error);
                if (!saved) App.DebugLog("Settings save uiLanguage failed: " + error);
            }
            L.SetLanguage(language);
            return saved;
        }

        public bool StartListener()
        {
            try { _app.CreateAndStartTransport(); return true; }
            catch (Exception ex) { App.DebugLog("Settings listener start failed: " + ex.Message); return false; }
        }
        public void StopListener() => _app.StopTransport();
        public void CopyPort() { if (CanCopyPort) { try { System.Windows.Clipboard.SetText(PortText); } catch { } } }

        private static string SaveFailed(string key) => SettingsText.Text("settings.footer.saveFailed", key + ": could not be saved", ("key", key));
        private void OnLanguageChanged(object sender, EventArgs e) => Post(() => LanguageChanged?.Invoke(this, EventArgs.Empty));
        private void OnToastEnabledChanged(object sender, EventArgs e) => Post(() => ToastEnabledChanged?.Invoke(this, EventArgs.Empty));
        private void OnPositionSaved(bool saved) => Post(() => PositionSaved?.Invoke(saved));
        private void Post(Action action)
        {
            if (_disposed) return;
            Action guarded = () => { if (!_disposed) action(); };
            try { if (_dispatcher.CheckAccess()) guarded(); else _dispatcher.BeginInvoke(guarded); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            L.Changed -= OnLanguageChanged;
            _app.ToastEnabledChanged -= OnToastEnabledChanged;
            _detachPosition?.Invoke();
        }
    }
}
