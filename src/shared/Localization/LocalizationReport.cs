using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Localization
{
    /// <summary>
    /// Writes the two per-locale sidecar files (spec §7.3):
    /// <c>_report.&lt;locale&gt;.json</c> — missing/rejected (deleted when both empty);
    /// <c>_active.&lt;locale&gt;.json</c> — every key's effective value + the locked list,
    /// the agent's read path for "which key renders this string".
    /// Neither matches <c>strings.*.json</c>, so neither can self-trigger the watcher.
    /// </summary>
    public static class LocalizationReport
    {
        public static string ReportPath(string dir, string locale)
            => Path.Combine(dir, "_report." + locale + ".json");

        public static string ActivePath(string dir, string locale)
            => Path.Combine(dir, "_active." + locale + ".json");

        public static void WriteAll(string dir, string locale,
            IReadOnlyDictionary<string, string> en,
            IReadOnlyDictionary<string, string> embeddedLocale,
            StringTable table,
            OverrideValidator.Result validation)
        {
            try { Directory.CreateDirectory(dir); }
            catch { return; }   // reporting must never break the plugin
            // isolate per file — a locked _report must not skip _active
            try { WriteReport(dir, locale, en, embeddedLocale, validation); } catch { }
            try { WriteActive(dir, locale, table); } catch { }
        }

        private static void WriteReport(string dir, string locale,
            IReadOnlyDictionary<string, string> en,
            IReadOnlyDictionary<string, string> embeddedLocale,
            OverrideValidator.Result validation)
        {
            var missing = new JArray();
            if (en != null)
            {
                // "missing" = en keys absent from the embedded LOCALE catalog.
                // en itself: embedded == en → nothing missing. Absent non-en catalog → all missing.
                var present = embeddedLocale
                    ?? (string.Equals(locale, "en", StringComparison.Ordinal)
                        ? en
                        : (IReadOnlyDictionary<string, string>)new Dictionary<string, string>());
                foreach (var kv in en)
                    if (!present.ContainsKey(kv.Key))
                        missing.Add(new JObject { ["key"] = kv.Key, ["en"] = kv.Value });
            }

            var rejected = new JArray();
            if (validation != null)
            {
                foreach (var r in validation.Rejected)
                    rejected.Add(new JObject { ["key"] = r.Key, ["reason"] = r.Value });
            }

            var path = ReportPath(dir, locale);
            if (missing.Count == 0 && rejected.Count == 0 &&
                (validation == null || validation.FileError == null))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            var report = new JObject
            {
                ["locale"] = locale,
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["missing"] = missing,
                ["rejected"] = rejected,
            };
            if (validation != null && validation.FileError != null)
                report["fileError"] = validation.FileError;

            File.WriteAllText(path, report.ToString(Formatting.Indented));
        }

        private static void WriteActive(string dir, string locale, StringTable table)
        {
            if (table == null) return;
            var strings = new JObject();
            foreach (var kv in table.EffectiveEntries)
                strings[kv.Key] = kv.Value;
            var locked = new JArray();
            foreach (var k in table.LockedKeys)
                locked.Add(k);

            var active = new JObject
            {
                ["locale"] = locale,
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["strings"] = strings,
                ["locked"] = locked,
            };
            File.WriteAllText(ActivePath(dir, locale), active.ToString(Formatting.Indented));
        }
    }
}
