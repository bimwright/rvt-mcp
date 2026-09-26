using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Localization
{
    public sealed class ToolSummaryResolution
    {
        public ToolSummaryResolution(string text, bool isFallback)
        {
            Text = text ?? string.Empty;
            IsFallback = isFallback;
        }

        public string Text { get; }
        public bool IsFallback { get; }
    }

    /// <summary>
    /// Short, Settings-only summaries for built-in MCP tools. This catalog is kept
    /// separate from StringTable because MCP tool names are protocol identifiers and
    /// must remain stable. Missing locale entries fall back to English, then to the
    /// server-provided English description; no network or runtime translation occurs.
    /// </summary>
    public sealed class ToolSummaryCatalog
    {
        public const int SupportedSchemaVersion = 1;
        public const string ResourcePrefix = "RvtMcp.ToolSummaries.summary.";

        private readonly IReadOnlyDictionary<string, string> _entries;

        private ToolSummaryCatalog(string locale, IReadOnlyDictionary<string, string> entries, int version)
        {
            Locale = locale ?? "en";
            Version = version;
            _entries = entries ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public string Locale { get; }
        public int Version { get; }
        public IReadOnlyDictionary<string, string> Entries => _entries;

        public bool TryGet(string mcpName, out string summary)
        {
            if (mcpName != null && _entries.TryGetValue(mcpName, out summary)) return true;
            summary = null;
            return false;
        }

        public string Resolve(string mcpName, string englishFallback)
        {
            string summary;
            return TryGet(mcpName, out summary) ? summary : (englishFallback ?? string.Empty);
        }

        public static ToolSummaryCatalog Load(Assembly assembly, string locale)
        {
            var normalized = LocaleResolver.NormalizeCode(locale);
            if (normalized == LocaleResolver.Auto) normalized = "en";
            try
            {
                var resource = ResourcePrefix + normalized + ".json";
                using (var stream = assembly?.GetManifestResourceStream(resource))
                using (var reader = stream == null ? null : new StreamReader(stream))
                {
                    if (reader == null) return Empty(normalized);
                    var root = JObject.Parse(reader.ReadToEnd());
                    var meta = root["_meta"] as JObject;
                    var version = meta?.Value<int?>("schema_version") ?? 0;
                    if (version != SupportedSchemaVersion) return Empty(normalized);
                    var entries = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var property in root.Properties())
                    {
                        if (property.Name == "_meta" || property.Value.Type != JTokenType.String) continue;
                        var value = property.Value.Value<string>();
                        if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(value)
                            && ScalarLength(value) <= 160)
                            entries[property.Name] = value;
                    }
                    return new ToolSummaryCatalog(normalized, entries, version);
                }
            }
            catch
            {
                return Empty(normalized);
            }
        }

        public static string Resolve(Assembly assembly, string locale, string mcpName, string englishFallback)
        {
            return ResolveWithFallback(assembly, locale, mcpName, englishFallback).Text;
        }

        public static ToolSummaryResolution ResolveWithFallback(Assembly assembly, string locale, string mcpName, string englishFallback)
        {
            var requested = Load(assembly, locale);
            string summary;
            if (requested.TryGet(mcpName, out summary)) return new ToolSummaryResolution(summary, false);
            if (!string.Equals(requested.Locale, "en", StringComparison.Ordinal)
                && Load(assembly, "en").TryGet(mcpName, out summary)) return new ToolSummaryResolution(summary, false);
            return new ToolSummaryResolution(englishFallback, true);
        }

        /// <summary>Checks all shipped locale resources against the English key set.</summary>
        public static IReadOnlyList<string> ValidateCompleteness(Assembly assembly, IEnumerable<string> locales = null)
        {
            var errors = new List<string>();
            var localeList = (locales ?? LocaleResolver.SupportedLocales).ToArray();
            var english = Load(assembly, "en");
            if (english.Entries.Count == 0) errors.Add("en: catalog is empty or invalid");
            foreach (var locale in localeList)
            {
                var table = Load(assembly, locale);
                foreach (var key in english.Entries.Keys)
                    if (!table.Entries.ContainsKey(key)) errors.Add(locale + ": missing " + key);
                foreach (var key in table.Entries.Keys)
                    if (!english.Entries.ContainsKey(key)) errors.Add(locale + ": unknown " + key);
            }
            return errors;
        }

        public static int ScalarLength(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            var count = 0;
            for (var i = 0; i < value.Length; i++, count++)
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            return count;
        }

        private static ToolSummaryCatalog Empty(string locale)
        {
            return new ToolSummaryCatalog(locale, new Dictionary<string, string>(StringComparer.Ordinal), 0);
        }
    }
}
