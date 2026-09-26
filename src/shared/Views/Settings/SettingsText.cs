using System;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>Localized lookup with an English fallback, shared by the Settings views.</summary>
    internal static class SettingsText
    {
        public static string Text(string key, string fallback, params (string Name, object Value)[] args)
        {
            var value = L.T(key, args);
            return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
        }
    }

    /// <summary>
    /// The Language group is intentionally invariant English per the Settings
    /// contract; these strings never go through StringTable.
    /// </summary>
    internal static class InvariantLanguageText
    {
        public const string Heading = "LANGUAGE";
        public const string AutoOption = "Auto — follow Revit";
        public const string CurrentlyUsingPrefix = "Currently using: ";
        public const string SourcePrefix = "Source: ";
        public const string SourceEnvironmentOverride = "Environment override";
        public const string SourceSavedSetting = "Saved setting";
        public const string RibbonCaption = "Language";
        public const string AutomationName = "Language";
        public const string AutomationHelp = "Select a session language. An environment override disables this control.";
        public const string OverrideToolTip = "Environment override is active; restart uses BIMWRIGHT_UI_LANGUAGE.";
        public const string SelectionToolTip = "Select a language; changes apply immediately.";
        public const string SessionOnlyWarning = "Environment override is active; this choice is session-only.";
    }
}
