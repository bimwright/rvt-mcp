using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        /// The file holds every call of the live session, but the in-memory log only
        /// keeps the newest <paramref name="liveSessionCount"/> (cap) — the evicted
        /// head of the current session is loaded back as view-only history.
        /// </summary>
        public static List<McpCallEntry> LoadPastSessions(
            string logDir, string currentSessionId, int liveSessionCount, int maxEntries = 200,
            IEnumerable<McpCallEntry> liveEntries = null)
        {
            if (maxEntries <= 0 || string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir)) return new List<McpCallEntry>();
            var liveIds = liveEntries == null ? null : new HashSet<string>(
                liveEntries.Where(e => !string.IsNullOrEmpty(e.CallId)).Select(e => e.CallId), StringComparer.Ordinal);

            // Archive names carry a timestamp (mcp-calls-yyyyMMdd-HHmmss.jsonl) so
            // ordinal sort = chronological order; the live file is always newest.
            var current = Path.Combine(logDir, "mcp-calls.jsonl");
            var files = new List<string>();
            foreach (var file in BoundedLogFile.Files(logDir, "mcp-calls"))
                if (!string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) files.Add(file);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) files.Add(current);

            // Bound memory while selecting by event time, including out-of-order appends.
            var retained = new SortedSet<Row>(Comparer<Row>.Create((a, b) =>
            {
                var byTime = a.Entry.Timestamp.CompareTo(b.Entry.Timestamp);
                return byTime != 0 ? byTime : a.Sequence.CompareTo(b.Sequence);
            }));
            var byId = new Dictionary<string, Row>(StringComparer.Ordinal);
            var capacity = (int)Math.Min(50000L, (long)maxEntries + (liveIds == null ? Math.Max(0, liveSessionCount) : 0));
            long sequence = 0;
            foreach (var file in files)
                foreach (var line in BoundedLogFile.ReadLinesSafe(file))
                {
                    var row = ParseLine(line);
                    if (row == null || liveIds?.Contains(row.Entry.CallId) == true) continue;
                    row.Sequence = sequence++;
                    if (byId.TryGetValue(row.Entry.CallId, out var duplicate)) retained.Remove(duplicate);
                    retained.Add(row);
                    byId[row.Entry.CallId] = row;
                    if (retained.Count > capacity)
                    {
                        var oldest = retained.Min;
                        retained.Remove(oldest);
                        byId.Remove(oldest.Entry.CallId);
                    }
                }

            var rows = new List<Row>(retained);

            // Hide exactly the live-session rows still in memory — the LAST
            // liveSessionCount of them; file order matches memory order.
            var currentIndexes = new List<int>();
            for (var i = 0; i < rows.Count; i++)
                if (string.Equals(rows[i].SessionId, currentSessionId, StringComparison.Ordinal))
                    currentIndexes.Add(i);
            // Count fallback is retained for callers without live identities (legacy logs).
            var hide = liveIds == null ? Math.Min(Math.Max(liveSessionCount, 0), currentIndexes.Count) : 0;
            for (var k = 0; k < hide; k++)
                rows[currentIndexes[currentIndexes.Count - 1 - k]].Hidden = true;

            var entries = new List<McpCallEntry>();
            foreach (var row in rows)
                if (!row.Hidden) entries.Add(row.Entry);

            if (entries.Count > maxEntries)
                entries.RemoveRange(0, entries.Count - maxEntries);

            for (var i = 0; i < entries.Count; i++)
                entries[i].Index = i - entries.Count;

            return entries;
        }

        private sealed class Row
        {
            public McpCallEntry Entry;
            public string SessionId;
            public bool Hidden;
            public long Sequence;
        }

        private static Row ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            try
            {
                var obj = JObject.Parse(line);
                var sessionId = obj.Value<string>("session_id");

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

                return new Row
                {
                    SessionId = sessionId,
                    Entry = new McpCallEntry
                    {
                        IsHistorical = true,
                        // Old files remain loadable without changing or migrating their contents.
                        CallId = obj.Value<string>("call_id") ?? "legacy:" + BakeRedactor.HashBody(line),
                        SessionTag = ShortSessionTag(sessionId),
                        Timestamp = timestamp,
                        ToolName = tool,
                        Success = success,
                        DurationMs = obj.Value<long?>("duration_ms") ?? 0,
                        ParamsJson = paramsJson,
                        ErrorMessage = error,
                        ResultJson = resultJson,
                        Summary = SummaryGenerator.Generate(tool, paramsJson, resultJson, success, error)
                    }
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
