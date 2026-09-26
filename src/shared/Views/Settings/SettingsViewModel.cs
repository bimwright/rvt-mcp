using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>
    /// Staged Settings state. Loading is deliberately read-only; only Apply and the
    /// immediate On/Off and language actions write preferences.
    /// </summary>
    public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly App _app;
        private readonly Dispatcher _dispatcher;
        private RvtMcpConfig _snapshot;
        private bool _toastEnabled;
        private int _toastIdleSeconds;
        private bool _persistSendCodeBodies;
        private int _persistSendCodeBodiesHours;
        private bool _cacheSendCodeBodies;
        private int _baselineToastIdleSeconds;
        private bool _baselinePersistSendCodeBodies;
        private int _baselinePersistSendCodeBodiesHours;
        private bool _baselineCacheSendCodeBodies;
        private bool _toastIdleDirty;
        private bool _persistSendCodeBodiesDirty;
        private bool _persistSendCodeBodiesHoursDirty;
        private bool _cacheSendCodeBodiesDirty;
        private string _selectedLanguage;
        private bool _disposed;
        private int _lastLanguageVersion;

        public SettingsViewModel(App app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _dispatcher = Dispatcher.CurrentDispatcher;
            _lastLanguageVersion = L.Version;
            ReloadReadOnly();
            L.Changed += OnLanguageChanged;
            _app.ToastEnabledChanged += OnAppToastEnabledChanged;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler LanguageChanged;

        public SettingsSnapshot Snapshot { get; private set; }
        public string SelectedLanguage => _selectedLanguage;
        public string EffectiveLanguage => L.Locale;
        public bool HasEnvironmentLanguageOverride =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RvtMcpConfig.EnvUiLanguage));
        public bool IsLanguageWriteDisabled => HasEnvironmentLanguageOverride;
        public bool ToastEnabled => _toastEnabled;
        public int ToastIdleSeconds
        {
            get => _toastIdleSeconds;
            set => SetStaged(ref _toastIdleSeconds, RvtMcpConfig.NormalizeToastIdleSeconds(value), ref _toastIdleDirty, nameof(ToastIdleSeconds));
        }

        public bool PersistSendCodeBodies
        {
            get => _persistSendCodeBodies;
            set => SetStaged(ref _persistSendCodeBodies, value, ref _persistSendCodeBodiesDirty, nameof(PersistSendCodeBodies));
        }

        public int PersistSendCodeBodiesHours
        {
            get => _persistSendCodeBodiesHours;
            set => SetStaged(ref _persistSendCodeBodiesHours, RvtMcpConfig.NormalizePersistSendCodeBodiesHours(value), ref _persistSendCodeBodiesHoursDirty, nameof(PersistSendCodeBodiesHours));
        }

        public bool CacheSendCodeBodies
        {
            get => _cacheSendCodeBodies;
            set => SetStaged(ref _cacheSendCodeBodies, value, ref _cacheSendCodeBodiesDirty, nameof(CacheSendCodeBodies));
        }
        public bool IsDirty { get; private set; }
        public string ImmediateWarning { get; private set; }

        public string RevitYear => Snapshot?.RevitYear ?? "—";
        public string TransportKind => Snapshot?.TransportKind ?? "—";
        public string ConnectionState
        {
            get
            {
                var transport = _app.Transport;
                if (transport == null || !_app.IsTransportRunning)
                    return SettingsText.Text("settings.general.state.listenerStopped", "Listener stopped");
                return transport.IsClientConnected
                    ? SettingsText.Text("settings.general.state.connected", "Connected")
                    : SettingsText.Text("settings.general.state.waiting", "Waiting for MCP client");
            }
        }
        public bool CanCopyPort => Snapshot != null &&
            string.Equals(Snapshot.TransportKind, "TCP", StringComparison.OrdinalIgnoreCase) &&
            _app.Transport is TcpTransportServer && _app.IsTransportRunning;
        public string PortText => CanCopyPort
            ? ((TcpTransportServer)_app.Transport).Port.ToString()
            : SettingsText.Text("settings.general.notApplicable", "Not applicable");

        public void ReloadReadOnly()
        {
            _snapshot = RvtMcpConfig.LoadReadOnly();
            _toastEnabled = _app.ToastEnabled;
            _toastIdleSeconds = _snapshot.ToastIdleSecondsOrDefault;
            _persistSendCodeBodies = _snapshot.IsPersistSendCodeBodiesActive();
            _persistSendCodeBodiesHours = _snapshot.PersistSendCodeBodiesHoursOrDefault;
            _cacheSendCodeBodies = _snapshot.CacheSendCodeBodiesOrDefault;
            _baselineToastIdleSeconds = _toastIdleSeconds;
            _baselinePersistSendCodeBodies = _persistSendCodeBodies;
            _baselinePersistSendCodeBodiesHours = _persistSendCodeBodiesHours;
            _baselineCacheSendCodeBodies = _cacheSendCodeBodies;
            _toastIdleDirty = false;
            _persistSendCodeBodiesDirty = false;
            _persistSendCodeBodiesHoursDirty = false;
            _cacheSendCodeBodiesDirty = false;
            _lastLanguageVersion = L.Version;
            _selectedLanguage = LocaleResolver.NormalizeCode(_snapshot.UiLanguage);

            var transport = _app.Transport;
            var info = transport?.ConnectionInfo ?? string.Empty;
            var isTcp = info.StartsWith("TCP:", StringComparison.OrdinalIgnoreCase);
            Snapshot = new SettingsSnapshot
            {
                LocaleRequested = _selectedLanguage,
                LocaleEffective = L.Locale,
                LocaleSource = HasEnvironmentLanguageOverride
                    ? InvariantLanguageText.SourceEnvironmentOverride
                    : (_snapshot.UiLanguage == null ? InvariantLanguageText.AutoOption : InvariantLanguageText.SourceSavedSetting),
                RevitYear = AuthToken.RevitVersion ?? "—",
                TransportKind = isTcp ? "TCP" : (info.StartsWith("Pipe:", StringComparison.OrdinalIgnoreCase) ? "Named Pipe" : "—"),
                IsDirty = false,
                ReadAtUtc = DateTimeOffset.UtcNow,
            };
            IsDirty = false;
            NotifyAll();
        }

        /// <summary>Apply only staged values. Toast On/Off and language are immediate.</summary>
        public SettingsApplyReport Apply()
        {
            var result = new SettingsApplyReport();
            var journalExpiredWhileOpen = false;
            if (_snapshot != null && _snapshot.IsPersistSendCodeBodiesActive())
            {
                try { journalExpiredWhileOpen = !RvtMcpConfig.LoadReadOnly().IsPersistSendCodeBodiesActive(); }
                catch { }
            }
            var patch = new RvtMcpConfig.SettingsPatch
            {
                ToastIdleSeconds = _toastIdleDirty ? (int?)ToastIdleSeconds : null,
                PersistSendCodeBodiesHours = _persistSendCodeBodiesHoursDirty ? (int?)PersistSendCodeBodiesHours : null,
                PersistSendCodeBodies = _persistSendCodeBodiesDirty ? (bool?)PersistSendCodeBodies : null,
                CacheSendCodeBodies = _cacheSendCodeBodiesDirty ? (bool?)CacheSendCodeBodies : null,
            };
            var applied = RvtMcpConfig.TryApplySettings(patch);
            foreach (var key in applied.Applied) result.Applied.Add(key);
            foreach (var failure in applied.Failed)
            {
                result.Failed.Add(SaveFailed(failure.Key));
                App.DebugLog("Settings apply failed for " + failure.Key + ": " + failure.Error);
            }
            result.JournalWasExpired = applied.JournalWasExpired;
            if (journalExpiredWhileOpen)
            {
                result.JournalWasExpired = true;
            }
            if (result.JournalWasExpired)
            {
                ImmediateWarning = SettingsText.Text("settings.footer.journalExpired",
                    "The send_code journal expired while Settings was open; it was not re-enabled.");
                OnPropertyChanged(nameof(ImmediateWarning));
            }
            RefreshEffectiveConfigAfterApply();
            if (applied.Applied.Contains("toastIdleSeconds") && _app.Config != null)
                _app.Config.ToastIdleSeconds = ToastIdleSeconds;
            if (applied.Applied.Contains("persistSendCodeBodiesHours") && _app.Config != null)
                _app.Config.PersistSendCodeBodiesHours = PersistSendCodeBodiesHours;
            if (applied.Applied.Contains("persistSendCodeBodies") && _app.Config != null)
                _app.Config.PersistSendCodeBodies = PersistSendCodeBodies;
            if (applied.Applied.Contains("cacheSendCodeBodies") && _app.Config != null)
                _app.Config.CacheSendCodeBodies = CacheSendCodeBodies;
            if (applied.Applied.Contains("toastIdleSeconds"))
            {
                _baselineToastIdleSeconds = ToastIdleSeconds;
                _toastIdleDirty = false;
            }
            if (applied.Applied.Contains("persistSendCodeBodiesHours"))
            {
                _baselinePersistSendCodeBodiesHours = PersistSendCodeBodiesHours;
                _persistSendCodeBodiesHoursDirty = false;
            }
            if (applied.Applied.Contains("persistSendCodeBodies"))
            {
                _baselinePersistSendCodeBodies = PersistSendCodeBodies;
                _persistSendCodeBodiesDirty = false;
            }
            if (applied.Applied.Contains("cacheSendCodeBodies"))
            {
                _baselineCacheSendCodeBodies = CacheSendCodeBodies;
                _cacheSendCodeBodiesDirty = false;
            }
            IsDirty = _toastIdleDirty || _persistSendCodeBodiesHoursDirty || _persistSendCodeBodiesDirty || _cacheSendCodeBodiesDirty;
            OnPropertyChanged(nameof(IsDirty));
            return result;
        }

        private void RefreshEffectiveConfigAfterApply()
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
            }
            catch
            {
                // The atomic writer already reported any write error. A refresh failure
                // must not turn a successful Apply into a second, misleading failure.
            }
        }

        public void SetToastEnabled(bool enabled)
        {
            _toastEnabled = enabled;
            _app.ToastEnabled = enabled;
            if (_app.Config != null) _app.Config.EnableToast = enabled;
            string error;
            var persisted = RvtMcpConfig.TrySaveEnableToast(enabled, out error);
            if (!persisted) App.DebugLog("Settings save enableToast failed: " + error);
            ImmediateWarning = persisted ? null : SaveFailed("enableToast");
            _app.ToastNotifier?.OnToastEnabledChanged(enabled, persisted);
            OnPropertyChanged(nameof(ToastEnabled));
            OnPropertyChanged(nameof(ImmediateWarning));
        }

        public void SetLanguage(string requested)
        {
            var code = LocaleResolver.NormalizeCode(requested);
            _selectedLanguage = code;
            if (!IsLanguageWriteDisabled)
            {
                string error;
                var persisted = RvtMcpConfig.TrySaveUiLanguage(code, out error);
                if (!persisted) App.DebugLog("Settings save uiLanguage failed: " + error);
                ImmediateWarning = persisted ? null : SaveFailed("uiLanguage");
            }
            else
            {
                ImmediateWarning = InvariantLanguageText.SessionOnlyWarning;
            }
            L.SetLanguage(code);
            OnPropertyChanged(nameof(SelectedLanguage));
            OnPropertyChanged(nameof(ImmediateWarning));
        }

        public void Cancel()
        {
            ReloadReadOnly();
        }

        public void CopyPort()
        {
            if (!CanCopyPort) return;
            try { System.Windows.Clipboard.SetText(PortText); } catch { }
        }

        private static string SaveFailed(string key)
        {
            return SettingsText.Text("settings.footer.saveFailed",
                key + ": could not be saved", ("key", key));
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            if (!_dispatcher.CheckAccess())
            {
                try { _dispatcher.BeginInvoke(new Action(() => OnLanguageChanged(null, EventArgs.Empty))); } catch { }
                return;
            }
            var version = L.Version;
            if (version <= _lastLanguageVersion) return;
            _lastLanguageVersion = version;
            Snapshot.LocaleEffective = L.Locale;
            OnPropertyChanged(nameof(EffectiveLanguage));
            OnPropertyChanged(nameof(Snapshot));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnAppToastEnabledChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            if (!_dispatcher.CheckAccess())
            {
                try { _dispatcher.BeginInvoke(new Action(() => OnAppToastEnabledChanged(null, EventArgs.Empty))); } catch { }
                return;
            }
            _toastEnabled = _app.ToastEnabled;
            OnPropertyChanged(nameof(ToastEnabled));
        }

        private void NotifyAll()
        {
            OnPropertyChanged(string.Empty);
            OnPropertyChanged(nameof(RevitYear));
            OnPropertyChanged(nameof(TransportKind));
            OnPropertyChanged(nameof(ConnectionState));
            OnPropertyChanged(nameof(PortText));
            OnPropertyChanged(nameof(CanCopyPort));
        }

        private void SetStaged<T>(ref T field, T value, ref bool dirty, string name)
        {
            if (Equals(field, value)) return;
            field = value;
            if (name == nameof(ToastIdleSeconds)) dirty = !Equals(value, _baselineToastIdleSeconds);
            else if (name == nameof(PersistSendCodeBodies)) dirty = !Equals(value, _baselinePersistSendCodeBodies);
            else if (name == nameof(PersistSendCodeBodiesHours)) dirty = !Equals(value, _baselinePersistSendCodeBodiesHours);
            else if (name == nameof(CacheSendCodeBodies)) dirty = !Equals(value, _baselineCacheSendCodeBodies);
            IsDirty = _toastIdleDirty || _persistSendCodeBodiesDirty || _persistSendCodeBodiesHoursDirty || _cacheSendCodeBodiesDirty;
            OnPropertyChanged(name);
            OnPropertyChanged(nameof(IsDirty));
        }

        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            L.Changed -= OnLanguageChanged;
            _app.ToastEnabledChanged -= OnAppToastEnabledChanged;
        }
    }
}
