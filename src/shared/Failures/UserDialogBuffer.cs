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
        public const int MaxItems = 20, MaxModels = 50, MaxText = 320, MaxReportBytes = 6144;
        public const string Note = "Already closed: the user answered these in Revit, outside MCP, before this call. Nothing is waiting. Revit does not report which button was chosen.";

        private sealed class Model
        {
            internal string Title;
            internal readonly List<JObject> Items = new List<JObject>();
            internal int Omitted;
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
            if (key == null || item == null) return;
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
                    && i.Value<string>("code") == bounded.Value<string>("code") && i.Value<string>("text") == bounded.Value<string>("text"));
                if (same != null) { same["count"] = same.Value<int>("count") + 1; return; }
                if (model.Items.Count >= MaxItems) { model.Omitted++; return; }
                bounded["count"] = 1;
                bounded["firstSeenUtc"] = now.UtcDateTime.ToString("o");
                model.Items.Add(bounded);
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
                    if (target.Items.Count >= MaxItems) { target.Omitted++; continue; }
                    target.Items.Add(item);
                }
                target.Omitted += source.Omitted;
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
                int taken = 0;
                foreach (var item in model.Items)
                {
                    items.Add(item);
                    if (taken > 0 && Bytes(report) > MaxReportBytes) { items.RemoveAt(items.Count - 1); break; }
                    taken++;
                }
                model.Items.RemoveRange(0, taken);
                if (model.Items.Count > 0) report["moreItems"] = model.Items.Count;
                if (model.Omitted > 0) { report["omittedItems"] = model.Omitted; model.Omitted = 0; }
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
