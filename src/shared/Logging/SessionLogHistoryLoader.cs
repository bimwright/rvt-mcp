using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Loads past-session entries from mcp-calls.jsonl (+ rotated archives) for the
    /// History window. File entries are already privacy-redacted at write time, so
    /// they bypass McpSessionLog.Add and go straight into Entries as read-only
    /// rows (IsHistorical = true, negative Index counting up toward the live session).
    /// </summary>
    public static class SessionLogHistoryLoader
    {
        /// <summary>
        /// Past-session entries, oldest first. Reads rotated archives (mcp-calls-*.jsonl)
        /// then the current log; keeps only the newest <paramref name="maxEntries"/>.
        /// </summary>
        public static List<McpCallEntry> LoadPastSessions(string logDir, string currentSessionId, int maxEntries = 1500)
        {
            var entries = new List<McpCallEntry>();
            if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir)) return entries;

            // Archive names carry a timestamp (mcp-calls-yyyyMMdd-HHmmss.jsonl) so
            // ordinal sort = chronological order; the live file is always newest.
            var files = new List<string>(Directory.GetFiles(logDir, "mcp-calls-*.jsonl"));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            var current = Path.Combine(logDir, "mcp-calls.jsonl");
            if (File.Exists(current)) files.Add(current);

            foreach (var file in files)
                foreach (var line in SafeReadAllLines(file))
                {
                    var entry = ParseLine(line, currentSessionId);
                    if (entry != null) entries.Add(entry);
                }

            if (entries.Count > maxEntries)
                entries.RemoveRange(0, entries.Count - maxEntries);

            for (var i = 0; i < entries.Count; i++)
                entries[i].Index = i - entries.Count;

            return entries;
        }

        private static string[] SafeReadAllLines(string path)
        {
            try { return File.ReadAllLines(path); }
            catch { return new string[0]; }
        }

        private static McpCallEntry ParseLine(string line, string currentSessionId)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                var obj = JObject.Parse(line);
                var sessionId = obj.Value<string>("session_id");
                if (!string.IsNullOrEmpty(currentSessionId)
                    && string.Equals(sessionId, currentSessionId, StringComparison.Ordinal))
                    return null; // live session rows already exist in memory

                var tool = obj.Value<string>("tool");
                if (string.IsNullOrEmpty(tool)) return null;

                var paramsToken = obj["params"];
                var paramsJson = paramsToken == null || paramsToken.Type == JTokenType.Null
                    ? null
                    : paramsToken.ToString(Formatting.None);
                var resultJson = obj.Value<string>("result");
                var error = obj.Value<string>("error");
                var success = obj.Value<bool?>("success") ?? false;

                // File log timestamps are UTC ("o"); session log uses local time.
                // AssumeUniversal|AdjustToUniversal is required: plain TryParse maps
                // the "Z" suffix to the *local* offset instead of UTC on .NET.
                var timestamp = DateTimeOffset.TryParse(obj.Value<string>("timestamp"),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var dto)
                    ? dto.LocalDateTime
                    : DateTime.MinValue;

                return new McpCallEntry
                {
                    IsHistorical = true,
                    SessionTag = ShortSessionTag(sessionId),
                    Timestamp = timestamp,
                    ToolName = tool,
                    Success = success,
                    DurationMs = obj.Value<long?>("duration_ms") ?? 0,
                    ParamsJson = paramsJson,
                    ErrorMessage = error,
                    ResultJson = resultJson,
                    Summary = SummaryGenerator.Generate(tool, paramsJson, resultJson, success, error)
                };
            }
            catch { return null; } // skip malformed lines
        }

        /// <summary>"yyyyMMdd-HHmmss-xxxx" → "MMdd-HHmm"; passthrough on other shapes.</summary>
        private static string ShortSessionTag(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return "?";
            if (sessionId.Length >= 13 && sessionId[8] == '-')
                return sessionId.Substring(4, 4) + "-" + sessionId.Substring(9, 4);
            return sessionId;
        }
    }
}
