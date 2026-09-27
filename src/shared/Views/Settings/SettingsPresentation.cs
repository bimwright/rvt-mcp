using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>UI boundary: the Revit adapter owns persistence; the window only presents state/actions.</summary>
    public interface ISettingsPresentation : INotifyPropertyChanged, IDisposable
    {
        event EventHandler LanguageChanged;
        SettingsSnapshot Snapshot { get; }
        string SelectedLanguage { get; }
        string EffectiveLanguage { get; }
        bool IsLanguageWriteDisabled { get; }
        bool ToastEnabled { get; }
        /// <summary>Wordmark on the activity card. Immediate and session-only; not staged or saved.</summary>
        bool ShowBranding { get; }
        int ToastIdleSeconds { get; set; }
        bool CacheSendCodeBodies { get; set; }
        bool PersistSendCodeBodies { get; set; }
        int PersistSendCodeBodiesHours { get; set; }
        bool IsDirty { get; }
        string ImmediateWarning { get; }
        string ImmediateWarningKey { get; }
        string RevitYear { get; }
        string TransportKind { get; }
        string ConnectionState { get; }
        bool IsListenerRunning { get; }
        bool IsClientConnected { get; }
        bool CanCopyPort { get; }
        string PortText { get; }
        void SetToastEnabled(bool enabled);
        /// <summary>Show or hide the activity-card wordmark for this Revit session.</summary>
        void SetShowBranding(bool show);
        void SetLanguage(string language);
        /// <summary>Same as the ribbon MCP toggle: start or stop the plugin listener.</summary>
        void SetListenerRunning(bool running);
        /// <summary>Stop and start the listener; it comes back with a new port (TCP) or pipe and token.</summary>
        void RestartListener();
        void CopyPort();
        SettingsApplyReport Apply();
        void Cancel();
    }

    public interface ISettingsToolsPresentation : INotifyPropertyChanged, IDisposable
    {
        ObservableCollection<ToolRow> Rows { get; }
        string CountsText { get; }
        string StatusText { get; }
        string StatusHelpText { get; }
        string SortColumn { get; }
        bool SortAscending { get; }
        void Refresh();
        void SortBy(string column);
    }
}
