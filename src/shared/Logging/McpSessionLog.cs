using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    public class McpCallEntry
    {
        public int Index { get; set; }
        public DateTime Timestamp { get; set; }
        public string ToolName { get; set; }
        public bool Success { get; set; }
        public long DurationMs { get; set; }
        public string ParamsJson { get; set; }
        public string ErrorMessage { get; set; }
        public string CodeSnippet { get; set; }
        // New fields for History redesign
        public string ResultJson { get; set; }
        public string Summary { get; set; }
        public string ToolDescription { get; set; }
        public int? RerunOfIndex { get; set; }
        /// <summary>ParamsJson exceeded the in-memory cap and was truncated — entry cannot be re-run.</summary>
        public bool ParamsTruncated { get; set; }
        /// <summary>Loaded from mcp-calls.jsonl (a previous session) — read-only, never re-runnable.</summary>
        public bool IsHistorical { get; set; }
        /// <summary>Short session tag for historical entries (e.g. "0923-1442").</summary>
        public string SessionTag { get; set; }
        /// <summary>Grid label: historical rows get a date prefix to separate them from live rows.</summary>
        public string TimeLabel => IsHistorical
            ? Timestamp.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public class McpSessionLog
    {
        private const int MaxParamsJsonLength = 64 * 1024;
        private const int MaxCodeSnippetLength = 128 * 1024;
        private const int MaxEntries = 1000;
        private int _nextIndex = 1;
        internal static Func<RvtMcpConfig> ConfigLoader = () => RvtMcpConfig.Load();

        public ObservableCollection<McpCallEntry> Entries { get; } = new ObservableCollection<McpCallEntry>();

        public event Action<McpCallEntry> EntryAdded;

        public void Add(McpCallEntry entry)
        {
            ApplyPrivacyPolicy(entry);
            entry.Index = _nextIndex++;
            if (entry.Timestamp == default)
                entry.Timestamp = DateTime.Now;
            // Historical rows are pinned, view-only, and have a separate loader cap.
            // Loading them must not evict live rows or make the next Add discard history.
            if (!entry.IsHistorical)
                while (Count >= MaxEntries)
                    Entries.Remove(Entries.First(e => !e.IsHistorical));
            Entries.Add(entry);
            EntryAdded?.Invoke(entry);
        }

        public void Clear()
        {
            // "Clear Session" drops live rows only — historical rows loaded from
            // the file log are not part of this session and stay visible.
            for (var i = Entries.Count - 1; i >= 0; i--)
                if (!Entries[i].IsHistorical)
                    Entries.RemoveAt(i);
            _nextIndex = 1;
        }

        // Entries is also edited directly by the history loader, so derive the live
        // count instead of maintaining a counter that can drift from the collection.
        public int Count => Entries.Count(e => !e.IsHistorical);

        private static void ApplyPrivacyPolicy(McpCallEntry entry)
        {
            if (entry == null)
                return;

            var isSendCode = string.Equals(entry.ToolName, "send_code_to_revit", StringComparison.OrdinalIgnoreCase);
            entry.ErrorMessage = McpResponsePrivacy.RedactErrorForResponse(entry.ErrorMessage);
            entry.Summary = BakeRedactor.RedactForBake(entry.Summary);
            entry.ResultJson = BakeRedactor.RedactForBake(entry.ResultJson, redactResultFields: isSendCode);

            if (!isSendCode)
            {
                // Bound in-memory size for fat payloads (e.g. batch_execute); the
                // file log caps params separately at 2KB.
                if (entry.ParamsJson != null && entry.ParamsJson.Length > MaxParamsJsonLength)
                {
                    entry.ParamsJson = entry.ParamsJson.Substring(0, MaxParamsJsonLength) + "... (truncated)";
                    entry.ParamsTruncated = true;
                }
                return;
            }

            var cacheBodies = false;
            try { cacheBodies = ConfigLoader?.Invoke()?.CacheSendCodeBodiesOrDefault ?? false; }
            catch { }

            if (cacheBodies)
            {
                // Re-run entries can omit the display copy. Recover it from the
                // executed params, never from an older (possibly truncated) snippet.
                if (string.IsNullOrEmpty(entry.CodeSnippet))
                {
                    try { entry.CodeSnippet = JObject.Parse(entry.ParamsJson ?? "{}").Value<string>("code"); }
                    catch { } // malformed/redacted params do not become executable code
                }
                // ParamsJson keeps the full body so re-run still works; bound only
                // the display copy (CodeSnippet feeds the INPUT code view).
                if (entry.CodeSnippet != null && entry.CodeSnippet.Length > MaxCodeSnippetLength)
                    entry.CodeSnippet = entry.CodeSnippet.Substring(0, MaxCodeSnippetLength) + "... (truncated)";
                return;
            }

            var code = ExtractCodeBody(entry.ParamsJson, entry.CodeSnippet);
            var codeHash = BakeRedactor.HashBody(code);
            entry.ParamsJson = JsonConvert.SerializeObject(new
            {
                code_hash = codeHash,
                code_length = code.Length
            }, Formatting.None);
            entry.CodeSnippet = null;
            // Recompute through the single summary path — the redacted params
            // carry code_hash, so Generate emits the locked security note.
            entry.Summary = SummaryGenerator.Generate(
                entry.ToolName, entry.ParamsJson, entry.ResultJson, entry.Success, entry.ErrorMessage);
        }

        /// <summary>
        /// Recompute Summary in the current language (L.Changed). Truncated params
        /// can't be reparsed — keep the stored summary. Output goes through the
        /// same bake-redaction as Add so recovered text can't leak paths/secrets.
        /// </summary>
        internal static void RefreshSummary(McpCallEntry entry)
        {
            if (entry == null || entry.ParamsTruncated)
                return;
            var summary = SummaryGenerator.Generate(
                entry.ToolName, entry.ParamsJson, entry.ResultJson, entry.Success, entry.ErrorMessage);
            entry.Summary = BakeRedactor.RedactForBake(summary);
        }

        private static string ExtractCodeBody(string paramsJson, string codeSnippet)
        {
            if (!string.IsNullOrEmpty(codeSnippet))
                return codeSnippet;
            if (string.IsNullOrEmpty(paramsJson))
                return string.Empty;

            try
            {
                var obj = JObject.Parse(paramsJson);
                var code = obj["code"];
                if (code == null || code.Type == JTokenType.Null)
                    return string.Empty;
                if (code.Type == JTokenType.String)
                    return code.Value<string>() ?? string.Empty;
                return code.ToString(Formatting.None);
            }
            catch
            {
                return paramsJson;
            }
        }
    }
}
