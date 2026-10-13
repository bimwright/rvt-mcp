using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Failures
{
    // Dialogs and failures the user handled in Revit outside any MCP call. Kept in memory per model and
    // reported once to the next MCP call on that model. Never a ledger record, an incident or a gate.
    public sealed class UserDialogBuffer
    {
        public const int MaxItems = 20, MaxModels = 50, MaxText = 320, MaxReportBytes = 6144, MaxFailuresRead = 5000;
        public const string Note = "Already closed: the user answered these in Revit, outside MCP, before this call. Nothing is waiting. Revit does not report which button was chosen.";

        private sealed class Model
        {
            internal string Title;
            internal readonly List<JObject> Items = new List<JObject>();
            internal int Omitted;
            internal int UnreadFailures;
            internal readonly Dictionary<string, int> OmittedFailures = new Dictionary<string, int>(StringComparer.Ordinal);
            internal bool OmittedFailureDetailsTruncated;
        }

        private sealed class FailureGroup<T>
        {
            internal string Code;
            internal T Sample;
            internal int Count;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Model> _models = new Dictionary<string, Model>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        public static string Key(string path, string title)
        {
            if (!string.IsNullOrWhiteSpace(path)) return "path:" + path.Trim().ToUpperInvariant();
            return string.IsNullOrWhiteSpace(title) ? null : "title:" + title.Trim();
        }

        public void Add(string key, string modelTitle, JObject item, DateTimeOffset now)
        {
            AddCounted(key, modelTitle, item, now, 1);
        }

        private void AddCounted(string key, string modelTitle, JObject item, DateTimeOffset now, int count)
        {
            if (key == null || item == null || count <= 0) return;
            // Some dialogs (Manage Links) come with no id ("0" or empty) and no text: nothing the agent could use.
            var code = item.Value<string>("code");
            if ((string.IsNullOrEmpty(code) || code == "0") && string.IsNullOrWhiteSpace(item.Value<string>("text"))) return;
            var bounded = (JObject)item.DeepClone();
            // The dialog is closed: button choices would read as a dialog still waiting for an answer.
            bounded.Remove("buttons"); bounded.Remove("commonButtons"); bounded.Remove("defaultButton");
            if (bounded["text"]?.Type == JTokenType.String) bounded["text"] = FailureCapture.SafeText(bounded.Value<string>("text"), MaxText);
            lock (_gate)
            {
                var model = Get(key, create: true);
                if (!string.IsNullOrWhiteSpace(modelTitle)) model.Title = FailureCapture.SafeText(modelTitle, 256);
                var same = model.Items.FirstOrDefault(i => i.Value<string>("kind") == bounded.Value<string>("kind")
                    && i.Value<string>("code") == bounded.Value<string>("code")
                    && (bounded.Value<string>("kind") == "failure" || i.Value<string>("text") == bounded.Value<string>("text")));
                if (same != null)
                {
                    same["count"] = same.Value<int>("count") + count;
                    if (bounded.Value<string>("countKind") == "at_least") same["countKind"] = "at_least";
                    return;
                }
                if (model.Items.Count >= MaxItems)
                { Omit(model, bounded.Value<string>("kind") == "failure" ? code : null, count); return; }
                bounded["count"] = count;
                bounded["firstSeenUtc"] = now.UtcDateTime.ToString("o");
                model.Items.Add(bounded);
            }
        }

        // Scan cheap native IDs first; read one representative text/severity per retained definition.
        // The ID cap bounds work on Revit's UI thread. Counts are lower bounds if any IDs remain unread.
        public void AddFailures<T>(string key, string modelTitle, IList<T> failures, Func<T, string> readCode,
            Func<T, string, JObject> read, DateTimeOffset now)
        {
            if (key == null || failures == null) return;
            var groups = new Dictionary<string, FailureGroup<T>>(StringComparer.Ordinal);
            var order = new List<FailureGroup<T>>();
            int unread = Math.Max(0, failures.Count - MaxFailuresRead);
            foreach (var failure in failures.Take(MaxFailuresRead))
            {
                string code;
                try { code = readCode(failure); } catch { unread++; continue; }
                if (string.IsNullOrWhiteSpace(code)) { unread++; continue; }
                if (!groups.TryGetValue(code, out var group))
                { group = new FailureGroup<T> { Code = code, Sample = failure }; groups.Add(code, group); order.Add(group); }
                group.Count++;
            }
            for (int i = 0; i < order.Count; i++)
            {
                var group = order[i];
                JObject item = null;
                if (i < MaxItems) { try { item = read(group.Sample, group.Code); } catch { } }
                if (item == null) { AddFailureOmission(key, modelTitle, group.Code, group.Count, false); continue; }
                item["kind"] = "failure"; item["code"] = group.Code;
                item["textIsSample"] = true; item["countKind"] = unread == 0 ? "exact" : "at_least";
                AddCounted(key, modelTitle, item, now, group.Count);
            }
            if (unread > 0) AddFailureOmission(key, modelTitle, null, unread, true);
        }

        private static void Omit(Model model, string code, int count)
        {
            model.Omitted += count;
            if (code == null) return;
            if (model.OmittedFailures.TryGetValue(code, out var previous)) model.OmittedFailures[code] = previous + count;
            else if (model.OmittedFailures.Count < MaxItems) model.OmittedFailures.Add(code, count);
            else model.OmittedFailureDetailsTruncated = true;
        }

        private void AddFailureOmission(string key, string modelTitle, string code, int count, bool unread)
        {
            lock (_gate)
            {
                var model = Get(key, create: true);
                if (!string.IsNullOrWhiteSpace(modelTitle)) model.Title = FailureCapture.SafeText(modelTitle, 256);
                Omit(model, code, count);
                if (unread) model.UnreadFailures += count;
            }
        }

        public void AddOmitted(string key, string modelTitle, int count)
        {
            if (key == null || count <= 0) return;
            lock (_gate)
            {
                var model = Get(key, create: true);
                if (!string.IsNullOrWhiteSpace(modelTitle)) model.Title = FailureCapture.SafeText(modelTitle, 256);
                model.Omitted += count;
            }
        }

        // A model opened under one path can end up under another (a new local copy of a central model).
        public void Rekey(string from, string to, string modelTitle)
        {
            if (from == null || to == null || from == to) return;
            lock (_gate)
            {
                if (!_models.TryGetValue(from, out var source)) return;
                _models.Remove(from); _order.Remove(from);
                var target = Get(to, create: true);
                if (!string.IsNullOrWhiteSpace(modelTitle)) target.Title = FailureCapture.SafeText(modelTitle, 256);
                else if (target.Title == null) target.Title = source.Title;
                foreach (var item in source.Items)
                {
                    if (target.Items.Count >= MaxItems)
                    { Omit(target, item.Value<string>("kind") == "failure" ? item.Value<string>("code") : null, item.Value<int>("count")); continue; }
                    target.Items.Add(item);
                }
                target.Omitted += source.Omitted;
                target.UnreadFailures += source.UnreadFailures;
                foreach (var pair in source.OmittedFailures)
                {
                    if (target.OmittedFailures.TryGetValue(pair.Key, out var previous)) target.OmittedFailures[pair.Key] = previous + pair.Value;
                    else if (target.OmittedFailures.Count < MaxItems) target.OmittedFailures.Add(pair.Key, pair.Value);
                    else target.OmittedFailureDetailsTruncated = true;
                }
                target.OmittedFailureDetailsTruncated |= source.OmittedFailureDetailsTruncated;
            }
        }

        public bool HasItems(string key)
        {
            lock (_gate) return key != null && _models.TryGetValue(key, out var model) && (model.Items.Count > 0 || model.Omitted > 0);
        }

        // Removes what it returns. Items that do not fit stay for the next call.
        public JObject Take(string key)
        {
            if (key == null) return null;
            lock (_gate)
            {
                if (!_models.TryGetValue(key, out var model) || (model.Items.Count == 0 && model.Omitted == 0)) return null;
                var items = new JArray();
                var report = new JObject { ["model"] = model.Title, ["items"] = items, ["note"] = Note };
                if (model.Omitted > 0) report["omittedItems"] = model.Omitted;
                if (model.UnreadFailures > 0) report["unreadFailures"] = model.UnreadFailures;
                if (model.OmittedFailures.Count > 0) report["omittedFailures"] = new JArray(model.OmittedFailures
                    .Select(p => new JObject { ["code"] = p.Key, ["count"] = p.Value }));
                if (model.OmittedFailureDetailsTruncated) report["omittedFailureDetailsTruncated"] = true;
                int taken = 0;
                foreach (var item in model.Items)
                {
                    items.Add(item);
                    report["moreItems"] = model.Items.Count - taken - 1;
                    if (taken > 0 && Bytes(report) > MaxReportBytes)
                    { items.RemoveAt(items.Count - 1); report["moreItems"] = model.Items.Count - taken; break; }
                    taken++;
                }
                model.Items.RemoveRange(0, taken);
                if (model.Items.Count > 0) report["moreItems"] = model.Items.Count;
                else report.Remove("moreItems");
                model.Omitted = 0; model.UnreadFailures = 0; model.OmittedFailures.Clear(); model.OmittedFailureDetailsTruncated = false;
                if (model.Items.Count == 0) { _models.Remove(key); _order.Remove(key); }
                return report;
            }
        }

        private Model Get(string key, bool create)
        {
            if (_models.TryGetValue(key, out var model) || !create) return model;
            if (_order.Count >= MaxModels) { _models.Remove(_order[0]); _order.RemoveAt(0); }
            model = new Model();
            _models[key] = model;
            _order.Add(key);
            return model;
        }

        private static int Bytes(JToken value) => Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
    }
}
