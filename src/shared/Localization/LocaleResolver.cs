using System.Collections.Generic;

namespace RvtMcp.Plugin.Localization
{
    /// <summary>
    /// Maps Revit's <c>LanguageType</c> enum name (taken as a string so no Revit API
    /// reference is needed) plus the merged <c>uiLanguage</c> config value to a shipped
    /// locale. Design: docs/superpowers/specs/2026-09-24-ui-localization-design.md §4.
    /// </summary>
    public static class LocaleResolver
    {
        public const string Auto = "auto";

        /// <summary>
        /// Closed set of shipped locales, in ribbon ComboBox display order
        /// (en right after auto, then CJK, then European).
        /// </summary>
        public static readonly IReadOnlyList<string> SupportedLocales = new[]
        {
            "en", "zh-CN", "zh-TW", "ja", "ko", "de", "fr", "es", "it",
            "nl", "pt-BR", "ru", "cs", "pl", "hu"
        };

        private static readonly HashSet<string> SupportedSet = new HashSet<string>(SupportedLocales);

        /// <summary>Revit LanguageType enum NAME → locale. Enum identical 2022–2027.</summary>
        private static readonly Dictionary<string, string> RevitToLocale = new Dictionary<string, string>
        {
            ["English_USA"] = "en",
            ["English_GB"] = "en",
            ["German"] = "de",
            ["Spanish"] = "es",
            ["French"] = "fr",
            ["Italian"] = "it",
            ["Dutch"] = "nl",
            ["Chinese_Simplified"] = "zh-CN",
            ["Chinese_Traditional"] = "zh-TW",
            ["Japanese"] = "ja",
            ["Korean"] = "ko",
            ["Russian"] = "ru",
            ["Czech"] = "cs",
            ["Polish"] = "pl",
            ["Hungarian"] = "hu",
            ["Brazilian_Portuguese"] = "pt-BR",
        };

        /// <summary>Native (endonym) display names for the ribbon ComboBox —
        /// invariant per locale, not translated.</summary>
        public static readonly IReadOnlyDictionary<string, string> NativeNames =
            new Dictionary<string, string>
            {
                ["en"] = "English",
                ["zh-CN"] = "简体中文",
                ["zh-TW"] = "繁體中文",
                ["ja"] = "日本語",
                ["ko"] = "한국어",
                ["de"] = "Deutsch",
                ["fr"] = "Français",
                ["es"] = "Español",
                ["it"] = "Italiano",
                ["nl"] = "Nederlands",
                ["pt-BR"] = "Português (Brasil)",
                ["ru"] = "Русский",
                ["cs"] = "Čeština",
                ["pl"] = "Polski",
                ["hu"] = "Magyar",
            };

        public static string NativeName(string code)
        {
            string name;
            return NativeNames.TryGetValue(code ?? string.Empty, out name) ? name : code;
        }

        /// <summary>
        /// Closed set, case-sensitive: "auto" or one of <see cref="SupportedLocales"/>.
        /// Anything else — including "vi" or "pt-br" — resolves to "auto".
        /// </summary>
        public static string NormalizeCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Auto;
            var trimmed = raw.Trim();
            if (trimmed == Auto) return Auto;
            return SupportedSet.Contains(trimmed) ? trimmed : Auto;
        }

        /// <summary>
        /// <paramref name="code"/> should already be normalized ("auto" or a supported
        /// code); an unrecognized code falls back to the auto mapping so callers may
        /// pass a raw config value without pre-normalizing.
        /// </summary>
        public static string ResolveLocale(string revitLanguageName, string code)
        {
            if (code != null && code != Auto && SupportedSet.Contains(code))
                return code;
            if (revitLanguageName != null && RevitToLocale.TryGetValue(revitLanguageName, out var locale))
                return locale;
            return "en";
        }
    }
}
