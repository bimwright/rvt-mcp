using System;
using System.Windows.Input;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Presentation.Settings;
using RvtMcp.Plugin.Localization;
using RvtMcp.Failures;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>
    /// Staged Settings state. Loading is deliberately read-only; only Apply and the
    /// immediate On/Off and language actions write preferences.
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject, ISettingsPresentation
    {
        private readonly ISettingsRuntime _runtime;
        private bool _toastEnabled;
        private bool _showBranding;
        private ToastPositionOptions _toastPosition = new ToastPositionOptions();
        private int _toastIdleSeconds;
        private bool _persistSendCodeBodies;
        private int _persistSendCodeBodiesHours;
        private bool _cacheSendCodeBodies;
        private string _warningPolicy;
        private string _baselineWarningPolicy;
        private bool _warningPolicyDirty;
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

        public SettingsViewModel(ISettingsRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            ApplyCommand = new RelayCommand(() => Apply(), () => !_disposed && IsDirty);
            DiscardCommand = new RelayCommand(Cancel, () => !_disposed && IsDirty);
            RestartListenerCommand = new RelayCommand(RestartListener, () => !_disposed && IsListenerRunning);
            CopyPortCommand = new RelayCommand(CopyPort, () => !_disposed && CanCopyPort);
            ResetToastPositionCommand = new RelayCommand(() => SetToastPosition(ToastPosition.WithOffset(null, null)),
                () => !_disposed && ToastEnabled && ToastPosition.HasOffset);
            try { ReloadReadOnly(); }
            catch { _runtime.Dispose(); throw; }
            _runtime.LanguageChanged += OnLanguageChanged;
            _runtime.ToastEnabledChanged += OnToastEnabledChanged;
            _runtime.PositionSaved += OnToastPositionSaved;
        }

        public event EventHandler LanguageChanged;

        public SettingsSnapshot Snapshot { get; private set; }
        public string SelectedLanguage { get => _selectedLanguage; set { if (value != null && value != _selectedLanguage) SetLanguage(value); } }
        public string EffectiveLanguage => _runtime.EffectiveLanguage;
        public bool HasEnvironmentLanguageOverride => _runtime.IsLanguageWriteDisabled;
        public bool IsLanguageWriteDisabled => HasEnvironmentLanguageOverride;
        public bool ToastEnabled { get => _toastEnabled; set { if (value != _toastEnabled) SetToastEnabled(value); } }
        public bool ShowBranding { get => _showBranding; set { if (value != _showBranding) SetShowBranding(value); } }
        public ToastPositionOptions ToastPosition => _toastPosition;
        public bool ToastRight { get => ToastPosition.Right; set { if (value != ToastRight) SetToastPosition(ToastPosition.WithCorner(value, ToastBottom)); } }
        public bool ToastBottom { get => ToastPosition.Bottom; set { if (value != ToastBottom) SetToastPosition(ToastPosition.WithCorner(ToastRight, value)); } }
        public bool ToastDragEnabled { get => ToastPosition.DragEnabled; set { if (value != ToastDragEnabled) SetToastPosition(ToastPosition.WithDrag(value)); } }
        public ICommand ApplyCommand { get; }
        public ICommand DiscardCommand { get; }
        public ICommand RestartListenerCommand { get; }
        public ICommand CopyPortCommand { get; }
        public ICommand ResetToastPositionCommand { get; }
        public SettingsApplyReport LastApplyReport { get; private set; }
        public string Feedback { get; private set; }

        public int ToastIdleSeconds
        {
            get => _toastIdleSeconds;
            set => SetStaged(ref _toastIdleSeconds, PreferenceValues.ToastIdleSeconds(value), ref _toastIdleDirty, nameof(ToastIdleSeconds));
        }

        public bool PersistSendCodeBodies
        {
            get => _persistSendCodeBodies;
            set => SetStaged(ref _persistSendCodeBodies, value, ref _persistSendCodeBodiesDirty, nameof(PersistSendCodeBodies));
        }

        public int PersistSendCodeBodiesHours
        {
            get => _persistSendCodeBodiesHours;
            set => SetStaged(ref _persistSendCodeBodiesHours, PreferenceValues.JournalHours(value), ref _persistSendCodeBodiesHoursDirty, nameof(PersistSendCodeBodiesHours));
        }

        public bool CacheSendCodeBodies
        {
            get => _cacheSendCodeBodies;
            set => SetStaged(ref _cacheSendCodeBodies, value, ref _cacheSendCodeBodiesDirty, nameof(CacheSendCodeBodies));
        }
        public bool IsWarningPolicyWriteDisabled => _runtime.IsWarningPolicyWriteDisabled;
        public string WarningPolicy
        {
            get => _warningPolicy;
            set
            {
                if (IsWarningPolicyWriteDisabled) return;
                SetStaged(ref _warningPolicy, WarningPolicyModes.Normalize(value), ref _warningPolicyDirty, nameof(WarningPolicy));
            }
        }
        public bool IsDirty { get; private set; }
        public string ImmediateWarning { get; private set; }
        public string ImmediateWarningKey { get; private set; }
        public bool IsListenerRunning { get => _runtime.IsListenerRunning; set => SetListenerRunning(value); }
        public bool IsClientConnected => _runtime.IsClientConnected;
        public string RevitYear => Snapshot?.RevitYear ?? "-";
        public string TransportKind => _runtime.TransportKind;
        public string ConnectionState => !IsListenerRunning
            ? SettingsText.Text("settings.general.state.listenerStopped", "Listener stopped")
            : SettingsText.Text(IsClientConnected ? "settings.general.state.connected" : "settings.general.state.waiting",
                IsClientConnected ? "Connected" : "Waiting for MCP client");
        public bool CanCopyPort => _runtime.CanCopyPort;
        public string PortText => _runtime.PortText;

        public void ReloadReadOnly()
        {
            var values = _runtime.Read();
            Snapshot = values.Snapshot;
            _toastEnabled = values.ToastEnabled;
            _showBranding = values.ShowBranding;
            _toastPosition = values.ToastPosition;
            _toastIdleSeconds = _baselineToastIdleSeconds = values.ToastIdleSeconds;
            _persistSendCodeBodies = _baselinePersistSendCodeBodies = values.PersistSendCodeBodies;
            _persistSendCodeBodiesHours = _baselinePersistSendCodeBodiesHours = values.PersistSendCodeBodiesHours;
            _cacheSendCodeBodies = _baselineCacheSendCodeBodies = values.CacheSendCodeBodies;
            _warningPolicy = _baselineWarningPolicy = values.WarningPolicy;
            _toastIdleDirty = _persistSendCodeBodiesDirty = _persistSendCodeBodiesHoursDirty = _cacheSendCodeBodiesDirty = _warningPolicyDirty = false;
            _lastLanguageVersion = _runtime.LanguageVersion;
            _selectedLanguage = values.SelectedLanguage;
            IsDirty = false;
            NotifyAll();
        }

        /// <summary>Apply only staged values. Toast On/Off and language are immediate.</summary>
        public SettingsApplyReport Apply()
        {
            var warningPolicyBlocked = _warningPolicyDirty && IsWarningPolicyWriteDisabled;
            var result = _runtime.Apply(new SettingsChanges
            {
                ToastIdleSeconds = _toastIdleDirty ? (int?)ToastIdleSeconds : null,
                PersistSendCodeBodiesHours = _persistSendCodeBodiesHoursDirty ? (int?)PersistSendCodeBodiesHours : null,
                PersistSendCodeBodies = _persistSendCodeBodiesDirty ? (bool?)PersistSendCodeBodies : null,
                CacheSendCodeBodies = _cacheSendCodeBodiesDirty ? (bool?)CacheSendCodeBodies : null,
                WarningPolicy = _warningPolicyDirty ? WarningPolicy : null,
            });
            if (result.JournalWasExpired)
            {
                ImmediateWarningKey = "persistSendCodeBodies";
                ImmediateWarning = SettingsText.Text("settings.footer.journalExpired",
                    "The send_code journal expired while Settings was open; it was not re-enabled.");
                OnPropertyChanged(nameof(ImmediateWarning));
            }
            if (result.Applied.Contains("toastIdleSeconds"))
            {
                _baselineToastIdleSeconds = ToastIdleSeconds;
                _toastIdleDirty = false;
            }
            if (result.Applied.Contains("persistSendCodeBodiesHours"))
            {
                _baselinePersistSendCodeBodiesHours = PersistSendCodeBodiesHours;
                _persistSendCodeBodiesHoursDirty = false;
            }
            if (result.Applied.Contains("persistSendCodeBodies"))
            {
                _baselinePersistSendCodeBodies = PersistSendCodeBodies;
                _persistSendCodeBodiesDirty = false;
            }
            if (result.Applied.Contains("cacheSendCodeBodies"))
            {
                _baselineCacheSendCodeBodies = CacheSendCodeBodies;
                _cacheSendCodeBodiesDirty = false;
            }
            if (result.Applied.Contains("warningPolicy") || warningPolicyBlocked)
            {
                _warningPolicy = _baselineWarningPolicy = _runtime.EffectiveWarningPolicy;
                _warningPolicyDirty = false;
                OnPropertyChanged(nameof(WarningPolicy));
            }
            IsDirty = _toastIdleDirty || _persistSendCodeBodiesHoursDirty || _persistSendCodeBodiesDirty || _cacheSendCodeBodiesDirty || _warningPolicyDirty;
            OnPropertyChanged(nameof(IsDirty));
            LastApplyReport = result;
            Feedback = result.Succeeded ? SettingsText.Text("settings.footer.applied", "Applied") : null;
            OnPropertyChanged(nameof(LastApplyReport));
            OnPropertyChanged(nameof(Feedback));
            RefreshCommands();
            return result;
        }

        public void SetToastEnabled(bool enabled)
        {
            _toastEnabled = enabled;
            var persisted = _runtime.SetToastEnabled(enabled);
            SetImmediateResult("enableToast", persisted);
            OnPropertyChanged(nameof(ToastEnabled));
            RefreshCommands();
            OnPropertyChanged(nameof(ImmediateWarning));
        }

        public void SetShowBranding(bool show)
        {
            _showBranding = show;
            var persisted = _runtime.SetShowBranding(show);
            SetImmediateResult("showBranding", persisted);
            OnPropertyChanged(nameof(ShowBranding));
            OnPropertyChanged(nameof(ImmediateWarning));
        }

        public void SetToastPosition(ToastPositionOptions options)
        {
            _toastPosition = options ?? new ToastPositionOptions();
            var persisted = _runtime.SetToastPosition(_toastPosition);
            SetImmediateResult("toastPosition", persisted);
            NotifyPosition();
            OnPropertyChanged(nameof(ImmediateWarning));
        }

        // A drag release is saved on the toast thread; mirror it here on the Settings thread.
        private void OnToastPositionSaved(bool saved)
        {
            if (_disposed) return;
            _toastPosition = _runtime.ToastPosition ?? _toastPosition;
            SetImmediateResult("toastPosition", saved);
            NotifyPosition();
            OnPropertyChanged(nameof(ImmediateWarning));
        }

        private void SetImmediateResult(string key, bool saved)
        {
            ImmediateWarningKey = saved ? null : key;
            ImmediateWarning = saved ? null : SaveFailed(key);
        }

        public void SetLanguage(string requested)
        {
            var code = LocaleResolver.NormalizeCode(requested);
            _selectedLanguage = code;
            var blocked = IsLanguageWriteDisabled;
            var saved = _runtime.SetLanguage(code);
            SetImmediateResult("uiLanguage", saved);
            if (blocked)
            {
                ImmediateWarningKey = "uiLanguage";
                ImmediateWarning = InvariantLanguageText.SessionOnlyWarning;
            }
            OnPropertyChanged(nameof(SelectedLanguage));
            OnPropertyChanged(nameof(ImmediateWarning));
        }

        public void Cancel()
        {
            LastApplyReport = null;
            Feedback = null;
            ReloadReadOnly();
        }

        public void SetListenerRunning(bool running)
        {
            if (running == IsListenerRunning) return;
            if (running) StartListener();
            else _runtime.StopListener();
            NotifyConnection();
        }

        public void RestartListener()
        {
            if (!IsListenerRunning) return;
            // CreateAndStartTransport stops the current listener first, then writes a new discovery file.
            StartListener();
            NotifyConnection();
        }

        private void StartListener()
        {
            if (_runtime.StartListener())
            {
                if (ImmediateWarningKey == "listener") ImmediateWarning = ImmediateWarningKey = null;
            }
            else
            {
                ImmediateWarningKey = "listener";
                ImmediateWarning = SettingsText.Text("settings.general.listenerFailed", "The MCP listener could not be started.");
            }
            OnPropertyChanged(nameof(ImmediateWarning));
        }

        private void NotifyConnection()
        {
            OnPropertyChanged(nameof(IsListenerRunning));
            OnPropertyChanged(nameof(IsClientConnected));
            OnPropertyChanged(nameof(ConnectionState));
            OnPropertyChanged(nameof(TransportKind));
            OnPropertyChanged(nameof(PortText));
            OnPropertyChanged(nameof(CanCopyPort));
            RefreshCommands();
        }

        public void CopyPort()
        {
            if (!CanCopyPort) return;
            _runtime.CopyPort();
        }

        private static string SaveFailed(string key)
        {
            return SettingsText.Text("settings.footer.saveFailed",
                key + ": could not be saved", ("key", key));
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            var version = _runtime.LanguageVersion;
            if (version <= _lastLanguageVersion) return;
            _lastLanguageVersion = version;
            Snapshot.LocaleEffective = EffectiveLanguage;
            OnPropertyChanged(nameof(EffectiveLanguage));
            OnPropertyChanged(nameof(Snapshot));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnToastEnabledChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            _toastEnabled = _runtime.ToastEnabled;
            OnPropertyChanged(nameof(ToastEnabled));
            RefreshCommands();
        }

        private void NotifyAll()
        {
            OnPropertyChanged(string.Empty);
            OnPropertyChanged(nameof(RevitYear));
            OnPropertyChanged(nameof(TransportKind));
            OnPropertyChanged(nameof(ConnectionState));
            OnPropertyChanged(nameof(PortText));
            OnPropertyChanged(nameof(CanCopyPort));
            RefreshCommands();
        }

        private void SetStaged<T>(ref T field, T value, ref bool dirty, string name)
        {
            if (Equals(field, value)) return;
            field = value;
            if (name == nameof(ToastIdleSeconds)) dirty = !Equals(value, _baselineToastIdleSeconds);
            else if (name == nameof(PersistSendCodeBodies)) dirty = !Equals(value, _baselinePersistSendCodeBodies);
            else if (name == nameof(PersistSendCodeBodiesHours)) dirty = !Equals(value, _baselinePersistSendCodeBodiesHours);
            else if (name == nameof(CacheSendCodeBodies)) dirty = !Equals(value, _baselineCacheSendCodeBodies);
            else if (name == nameof(WarningPolicy)) dirty = !Equals(value, _baselineWarningPolicy);
            IsDirty = _toastIdleDirty || _persistSendCodeBodiesDirty || _persistSendCodeBodiesHoursDirty || _cacheSendCodeBodiesDirty || _warningPolicyDirty;
            if (IsDirty) Feedback = null;
            OnPropertyChanged(name);
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(Feedback));
            RefreshCommands();
        }

        private void NotifyPosition()
        {
            OnPropertyChanged(nameof(ToastPosition));
            OnPropertyChanged(nameof(ToastRight));
            OnPropertyChanged(nameof(ToastBottom));
            OnPropertyChanged(nameof(ToastDragEnabled));
            RefreshCommands();
        }

        private void RefreshCommands()
        {
            ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DiscardCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RestartListenerCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CopyPortCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ResetToastPositionCommand).RaiseCanExecuteChanged();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _runtime.LanguageChanged -= OnLanguageChanged;
            _runtime.ToastEnabledChanged -= OnToastEnabledChanged;
            _runtime.PositionSaved -= OnToastPositionSaved;
            _runtime.Dispose();
            RefreshCommands();
        }
    }
}
