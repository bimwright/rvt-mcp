using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Survey
{
    /// <summary>UI-thread RAM only. A selection is provenance, never an agent-supplied before value.</summary>
    public sealed class SurveySnapshotCache
    {
        public const int SessionLimit = 8;
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
        private sealed class Entry
        {
            public object Document;
            public string Path, SurveyId;
            public DateTimeOffset Created;
            public JArray Targets;
        }
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private string _invalidated = "no_current_survey";

        public void Clear(string reason)
        {
            _entries.Clear();
            _invalidated = reason;
        }

        public void Remember(string session, object document, string path, string surveyId, JArray targets, DateTimeOffset now)
        {
            if (!ChangeHistoryTransfer.IsId(session) || !ChangeHistoryTransfer.IsId(surveyId) || document == null
                || string.IsNullOrWhiteSpace(path) || targets == null || targets.Count == 0 || targets.Count > 25)
                throw new ArgumentException("Invalid survey history selection.");
            foreach (var key in _entries.Where(p => now < p.Value.Created || now - p.Value.Created > Lifetime).Select(p => p.Key).ToArray())
                _entries.Remove(key);
            if (!_entries.ContainsKey(session) && _entries.Count >= SessionLimit)
                _entries.Remove(_entries.OrderBy(p => p.Value.Created).First().Key);
            _entries[session] = new Entry { Document = document, Path = path, SurveyId = surveyId, Created = now, Targets = (JArray)targets.DeepClone() };
        }

        public JObject Take(string session, object document, string path, DateTimeOffset now)
        {
            if (session == null || !_entries.TryGetValue(session, out var entry)) return Unavailable(_invalidated);
            _entries.Remove(session);
            if (!Equals(entry.Document, document) || !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                return Unavailable("document_context_changed");
            if (now < entry.Created || now - entry.Created > Lifetime) return Unavailable("survey_expired");
            return new JObject { ["status"] = "selected", ["surveyId"] = entry.SurveyId,
                ["surveyedAt"] = entry.Created.ToString("o"), ["targets"] = entry.Targets.DeepClone() };
        }

        public static JObject Unavailable(string reason) => new JObject { ["status"] = "unavailable", ["reason"] = reason };

        // Shared with persistence tests: only exact captured modified membership can carry survey values.
        public static JObject Match(JObject snapshot, JObject element)
        {
            if (snapshot?.Value<string>("status") != "captured" || element?.Value<string>("kind") != "modified"
                || string.IsNullOrEmpty(element.Value<string>("uniqueId"))) return null;
            return (snapshot["elements"] as JArray)?.OfType<JObject>().SingleOrDefault(row =>
                row.Value<long>("elementId") == element.Value<long>("elementId")
                && row.Value<string>("uniqueId") == element.Value<string>("uniqueId"));
        }
    }
}
