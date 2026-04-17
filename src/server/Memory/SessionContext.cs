using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RevitMcp.Server.Memory
{
    public class SessionContext
    {
        private readonly List<JournalEntry> _entries = new List<JournalEntry>();
        private readonly object _lock = new object();
        private readonly PatternDetector _patterns = new PatternDetector();
        private readonly JournalLogger _journal = new JournalLogger();
        private readonly string _sessionId;
        private readonly DateTime _startTime;

        public JournalLogger Journal => _journal;
        public PatternDetector Patterns => _patterns;

        public SessionContext()
        {
            _sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                         Guid.NewGuid().ToString("N").Substring(0, 4);
            _startTime = DateTime.UtcNow;
        }

        public void RecordCall(string tool, string paramsJson, bool success,
            long durationMs, string error = null, string resultJson = null)
        {
            var entry = JournalEntry.Create(tool, paramsJson, success, durationMs, error, resultJson);
            lock (_lock) { _entries.Add(entry); }
            _patterns.Record(tool, success);
            _journal.Log(entry);
        }

        public string GetSummary()
        {
            List<JournalEntry> snapshot;
            lock (_lock) { snapshot = _entries.ToList(); }

            var elapsed = DateTime.UtcNow - _startTime;
            var totalCalls = snapshot.Count;
            var successCount = snapshot.Count(e => e.Success);
            var recentCalls = snapshot.Skip(Math.Max(0, snapshot.Count - 10)).ToList();
            var toolCounts = snapshot.GroupBy(e => e.Tool)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => new { tool = g.Key, count = g.Count() })
                .ToList();

            var report = _patterns.GetReport();

            var summary = new
            {
                session_id = _sessionId,
                uptime_minutes = (int)elapsed.TotalMinutes,
                total_calls = totalCalls,
                success_rate = totalCalls > 0 ? $"{(double)successCount / totalCalls:P0}" : "N/A",
                top_tools = toolCounts,
                recent_calls = recentCalls.Select(e => new
                {
                    tool = e.Tool,
                    success = e.Success,
                    duration_ms = e.DurationMs,
                    error = e.Error
                }),
                flags = report.Flags
            };

            return JsonConvert.SerializeObject(summary, Formatting.Indented);
        }

        public PatternReport GetPatternReport() => _patterns.GetReport();

        public int CallCount { get { lock (_lock) { return _entries.Count; } } }
    }
}
