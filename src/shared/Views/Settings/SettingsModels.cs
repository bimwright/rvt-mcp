using System;

namespace RvtMcp.Plugin.Views.Settings
{
    public enum SettingsTab
    {
        General,
        Toast,
        Tools,
        About
    }

    /// <summary>Read-only connection/configuration facts shown by the Settings shell.</summary>
    public sealed class SettingsSnapshot
    {
        public string LocaleRequested { get; set; }
        public string LocaleEffective { get; set; }
        public string LocaleSource { get; set; }
        public string RevitYear { get; set; }
        public string TransportKind { get; set; }
        public bool IsDirty { get; set; }
        public DateTimeOffset ReadAtUtc { get; set; }
    }

    /// <summary>One staged Settings apply result. Errors are safe for UI display.</summary>
    public sealed class SettingsApplyReport
    {
        public SettingsApplyReport()
        {
            Applied = new System.Collections.Generic.List<string>();
            Failed = new System.Collections.Generic.List<string>();
        }

        public System.Collections.Generic.IList<string> Applied { get; private set; }
        public System.Collections.Generic.IList<string> Failed { get; private set; }
        public bool Succeeded => Failed.Count == 0;
        public bool JournalWasExpired { get; set; }
    }
}
