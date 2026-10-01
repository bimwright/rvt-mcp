using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Net element membership for committed events in one MCP call, not a parameter diff.</summary>
    public sealed class DocumentChangeAccumulator
    {
        public const int IdLimit = 200;
        public const int TrackingLimit = 50000;
        private readonly Dictionary<long, string> _kinds = new Dictionary<long, string>();
        private readonly Dictionary<long, string> _categories = new Dictionary<long, string>();
        private readonly Dictionary<long, string> _uniqueIds = new Dictionary<long, string>();
        private readonly List<string> _transactions = new List<string>();
        private string _incomplete;
        private bool _namesTruncated;

        public void MarkIncomplete(string reason) { _incomplete = reason; }

        public void AddTransaction(string name)
        {
            if (_transactions.Count >= 20) { _namesTruncated = true; return; }
            name = BakeRedactor.RedactForBake(name ?? "");
            if (name.Length > 160) { name = name.Substring(0, 160); _namesTruncated = true; }
            _transactions.Add(name);
        }

        public void Observe(long id, string kind, string category = null, string uniqueId = null)
        {
            if (!_kinds.ContainsKey(id) && _kinds.Count >= TrackingLimit)
            {
                MarkIncomplete("Tracking limit exceeded; the final change set is unknown.");
                return;
            }
            _kinds.TryGetValue(id, out var previous);
            if (previous == "added" && kind == "deleted")
            {
                _kinds.Remove(id);
                _categories.Remove(id);
                _uniqueIds.Remove(id);
                return;
            }
            _kinds[id] = previous == "added" && kind == "modified" ? "added" : kind;
            if (category != null) _categories[id] = category;
            if (uniqueId != null) _uniqueIds[id] = uniqueId;
        }

        public JArray HistoryElements()
        {
            // Unobservable rollback invalidates the alleged final set, not just the public summary.
            if (_incomplete != null) return new JArray();
            return new JArray(_kinds.OrderBy(p => p.Key).Select(p => new JObject
            {
                ["elementId"] = p.Key, ["kind"] = p.Value,
                ["uniqueId"] = _uniqueIds.TryGetValue(p.Key, out var uniqueId) ? uniqueId : null,
                ["category"] = _categories.TryGetValue(p.Key, out var category) ? category : null
            }));
        }

        public JObject Snapshot(string document)
        {
            if (_kinds.Count == 0 && _incomplete == null) return null;
            var result = new JObject
            {
                ["document"] = BakeRedactor.RedactForBake(document ?? ""),
                ["status"] = _incomplete == null ? "complete" : "incomplete",
                ["transactions"] = new JArray(_transactions),
                ["truncated"] = _namesTruncated
            };
            if (_incomplete != null) result["note"] = _incomplete;
            foreach (var kind in new[] { "added", "modified", "deleted" })
            {
                // After an unobservable group rollback, observed IDs are not evidence of final changes.
                var ids = _incomplete == null
                    ? _kinds.Where(p => p.Value == kind).Select(p => p.Key).OrderBy(x => x).ToArray()
                    : Array.Empty<long>();
                result[kind] = new JObject
                {
                    ["count"] = _incomplete == null ? new JValue(ids.Length) : JValue.CreateNull(),
                    ["ids"] = new JArray(ids.Take(IdLimit))
                };
                if (ids.Length > IdLimit) result["truncated"] = true;
            }
            var categories = new JObject();
            if (_incomplete == null)
            {
                foreach (var group in _kinds.Keys.GroupBy(id => _categories.TryGetValue(id, out var c) ? c : "unknown"))
                    categories[group.Key] = group.Count();
            }
            result["by_category"] = categories;
            return result;
        }
    }
}
