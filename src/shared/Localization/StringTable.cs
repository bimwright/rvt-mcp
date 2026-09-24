using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Localization
{
    /// <summary>
    /// Immutable lookup table for one locale. Lookup order:
    /// override[locale] → embedded[locale] → embedded[en]. A key missing everywhere
    /// returns the key itself and is logged once. Placeholders: <c>{name}</c> fills
    /// invariant, <c>{name:n}</c> formats with the locale culture (counts/measurements
    /// only — ids, scales, ports, hashes stay invariant). Never throws.
    /// Spec: docs/superpowers/specs/2026-09-24-ui-localization-design.md §5.1.
    /// </summary>
    public sealed class StringTable
    {
        public const string LockedPrefix = "security.";

        private readonly IReadOnlyDictionary<string, string> _overrides;
        private readonly IReadOnlyDictionary<string, string> _embedded;
        private readonly IReadOnlyDictionary<string, string> _en;
        private readonly CultureInfo _culture;
        private readonly Action<string> _log;
        private readonly object _logLock = new object();
        private readonly HashSet<string> _logged = new HashSet<string>();

        public string Locale { get; }

        /// <summary>Merged effective entries (en ← embedded ← override) for the _active dump.</summary>
        public IReadOnlyDictionary<string, string> EffectiveEntries { get; }

        /// <summary>en keys under <see cref="LockedPrefix"/> — the explicit locked list.</summary>
        public IReadOnlyList<string> LockedKeys { get; }

        private StringTable(string locale,
            IReadOnlyDictionary<string, string> en,
            IReadOnlyDictionary<string, string> embedded,
            IReadOnlyDictionary<string, string> overrides,
            CultureInfo culture, Action<string> log)
        {
            Locale = locale;
            _en = en;
            _embedded = embedded;
            _overrides = overrides;
            _culture = culture;
            _log = log;

            var effective = new Dictionary<string, string>(StringComparer.Ordinal);
            if (en != null) foreach (var kv in en) effective[kv.Key] = kv.Value;
            if (embedded != null) foreach (var kv in embedded) effective[kv.Key] = kv.Value;
            if (overrides != null) foreach (var kv in overrides) effective[kv.Key] = kv.Value;
            EffectiveEntries = effective;

            var locked = new List<string>();
            if (en != null)
                foreach (var k in en.Keys)
                    if (k.StartsWith(LockedPrefix, StringComparison.Ordinal))
                        locked.Add(k);
            locked.Sort(StringComparer.Ordinal);
            LockedKeys = locked;
        }

        public static StringTable Build(string locale,
            IReadOnlyDictionary<string, string> embeddedEn,
            IReadOnlyDictionary<string, string> embeddedLocale,
            IReadOnlyDictionary<string, string> acceptedOverrides,
            Action<string> log = null)
        {
            CultureInfo culture;
            try { culture = CultureInfo.GetCultureInfo(locale ?? "en"); }
            catch { culture = CultureInfo.InvariantCulture; }
            return new StringTable(locale ?? "en", embeddedEn,
                embeddedLocale, acceptedOverrides, culture, log);
        }

        /// <summary>Parses a catalog file body. <c>_meta</c> and non-string values are skipped.
        /// Throws on invalid JSON — the caller maps that to <c>invalid_json</c>.</summary>
        public static IReadOnlyDictionary<string, string> ParseCatalog(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var root = JObject.Parse(json);
            foreach (var prop in root.Properties())
            {
                if (prop.Name == "_meta") continue;
                if (prop.Value.Type == JTokenType.String)
                    result[prop.Name] = prop.Value.Value<string>();
            }
            return result;
        }

        /// <summary>Never throws. Missing key → returns the key, logged once.</summary>
        public string T(string key, params (string Name, object Value)[] args)
        {
            if (key == null) return string.Empty;
            if (!TryGetRaw(key, out var template))
            {
                LogOnce("missing:" + key, "[l10n] missing key: " + key);
                return key;
            }
            if (args == null || args.Length == 0 || template.IndexOf('{') < 0)
                return template;
            return Fill(key, template, args);
        }

        private bool TryGetRaw(string key, out string value)
        {
            if (_overrides != null && _overrides.TryGetValue(key, out value)) return true;
            if (_embedded != null && _embedded.TryGetValue(key, out value)) return true;
            if (_en != null && _en.TryGetValue(key, out value)) return true;
            value = null;
            return false;
        }

        private string Fill(string key, string template, (string Name, object Value)[] args)
        {
            var sb = new StringBuilder(template.Length + 16);
            var i = 0;
            while (i < template.Length)
            {
                var open = template.IndexOf('{', i);
                if (open < 0) { sb.Append(template, i, template.Length - i); break; }
                var close = template.IndexOf('}', open + 1);
                if (close < 0) { sb.Append(template, i, template.Length - i); break; }
                sb.Append(template, i, open - i);
                var token = template.Substring(open + 1, close - open - 1);
                var name = token;
                var cultureFormat = false;
                if (token.EndsWith(":n", StringComparison.Ordinal))
                {
                    name = token.Substring(0, token.Length - 2);
                    cultureFormat = true;
                }
                var found = false;
                object value = null;
                for (var a = 0; a < args.Length; a++)
                {
                    if (args[a].Name == name) { value = args[a].Value; found = true; break; }
                }
                if (!found)
                {
                    LogOnce("arg:" + key + ":" + token,
                        "[l10n] missing argument {" + token + "} for key: " + key);
                    sb.Append('{').Append(token).Append('}');
                }
                else
                {
                    sb.Append(FormatValue(value, cultureFormat));
                }
                i = close + 1;
            }
            return sb.ToString();
        }

        private string FormatValue(object value, bool cultureFormat)
        {
            if (value == null) return string.Empty;
            if (cultureFormat && value is IFormattable formattable)
            {
                var integral = value is byte || value is sbyte || value is short || value is ushort
                    || value is int || value is uint || value is long || value is ulong;
                // "N"/"N0" throws FormatException on non-numeric IFormattables
                // (DateTime, TimeSpan) — T() must never throw, fall back to invariant.
                try { return formattable.ToString(integral ? "N0" : "N", _culture); }
                catch { }
            }
            try { return Convert.ToString(value, CultureInfo.InvariantCulture); }
            catch { return string.Empty; }
        }

        /// <summary>Raw <c>{...}</c> token set of a template, including any ":n" suffix.
        /// Shared with OverrideValidator for placeholder-set comparison.</summary>
        internal static HashSet<string> ExtractTokens(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (text == null) return set;
            var i = 0;
            while (i < text.Length)
            {
                var open = text.IndexOf('{', i);
                if (open < 0) break;
                var close = text.IndexOf('}', open + 1);
                if (close < 0) break;
                set.Add(text.Substring(open + 1, close - open - 1));
                i = close + 1;
            }
            return set;
        }

        private void LogOnce(string dedupeKey, string message)
        {
            lock (_logLock)
            {
                if (!_logged.Add(dedupeKey)) return;
            }
            try { _log?.Invoke(message); } catch { }
        }
    }
}
