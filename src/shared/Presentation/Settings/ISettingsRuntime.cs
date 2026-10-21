using System;
using RvtMcp.Plugin.Views.Settings;

namespace RvtMcp.Plugin.Presentation.Settings
{
    /// <summary>A per-window adapter. Disposing it detaches observations, never stops shared host resources.</summary>
    public interface ISettingsRuntime : IDisposable
    {
        event EventHandler ConnectionChanged;
        event EventHandler LanguageChanged;
        event EventHandler ToastEnabledChanged;
        event Action<bool> PositionSaved;
        int LanguageVersion { get; }
        string EffectiveLanguage { get; }
        bool IsLanguageWriteDisabled { get; }
        bool IsWarningPolicyWriteDisabled { get; }
        bool ToastEnabled { get; }
        ToastPositionOptions ToastPosition { get; }
        bool IsListenerRunning { get; }
        bool IsClientConnected { get; }
        string TransportKind { get; }
        bool CanCopyPort { get; }
        string PortText { get; }
        string EffectiveWarningPolicy { get; }
        SettingsValues Read();
        SettingsApplyReport Apply(SettingsChanges changes);
        bool SetToastEnabled(bool enabled);
        bool SetShowBranding(bool show);
        bool SetToastPosition(ToastPositionOptions options);
        bool SetLanguage(string language);
        bool StartListener();
        void StopListener();
        void CopyPort();
    }

    public sealed class SettingsValues
    {
        public SettingsSnapshot Snapshot { get; set; }
        public string SelectedLanguage { get; set; }
        public bool ToastEnabled { get; set; }
        public bool ShowBranding { get; set; }
        public ToastPositionOptions ToastPosition { get; set; }
        public int ToastIdleSeconds { get; set; }
        public bool PersistSendCodeBodies { get; set; }
        public int PersistSendCodeBodiesHours { get; set; }
        public bool CacheSendCodeBodies { get; set; }
        public string WarningPolicy { get; set; }
    }

    /// <summary>Only staged fields; null means leave the existing preference alone.</summary>
    public sealed class SettingsChanges
    {
        public int? ToastIdleSeconds { get; set; }
        public bool? PersistSendCodeBodies { get; set; }
        public int? PersistSendCodeBodiesHours { get; set; }
        public bool? CacheSendCodeBodies { get; set; }
        public string WarningPolicy { get; set; }
    }
}
