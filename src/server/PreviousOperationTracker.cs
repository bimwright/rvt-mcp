using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>
    /// Final results of operations whose caller only received a pending receipt. The next tool response carries
    /// each one once (<c>_previousOperation</c>), because an agent may never query the status itself.
    /// </summary>
    internal sealed class PreviousOperationTracker
    {
        internal const int MaxPerResponse = 3, MaxTracked = 20, MaxDataBytes = 8192;
        private sealed class Entry
        {
            internal string OperationId, Tool;
            internal Task<string> Execution;
        }

        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();

        internal void Remember(string operationId, string tool, Task<string> execution)
        {
            if (string.IsNullOrEmpty(operationId) || execution == null) return;
            lock (_gate)
            {
                if (_entries.Any(e => e.OperationId == operationId)) return;
                if (_entries.Count >= MaxTracked) _entries.RemoveAt(0);
                _entries.Add(new Entry { OperationId = operationId, Tool = tool, Execution = execution });
            }
        }

        /// <summary>The agent read the final status itself. A result the server has not received yet still follows later.</summary>
        internal void MarkRead(string operationId)
        {
            lock (_gate) _entries.RemoveAll(e => e.OperationId == operationId && e.Execution.IsCompleted);
        }

        /// <summary>
        /// The command's own result, bounded, while this session still holds it. The ledger keeps receipts only,
        /// never a command result, so this is the one place a status query can find it.
        /// </summary>
        internal JToken FinalData(string operationId)
        {
            Task<string> execution;
            lock (_gate) execution = _entries.FirstOrDefault(e => e.OperationId == operationId)?.Execution;
            if (execution == null || execution.Status != TaskStatus.RanToCompletion) return null;
            try { return BoundedData(JObject.Parse(execution.Result)["data"]); } catch { return null; }
        }

        internal void Clear()
        {
            lock (_gate) _entries.Clear();
        }

        internal void Attach(JObject response)
        {
            if (response == null) return;
            List<Entry> finished;
            lock (_gate)
            {
                // A lost connection ends the wait without a result; the local ledger still answers status queries.
                _entries.RemoveAll(e => e.Execution.IsCanceled || e.Execution.IsFaulted);
                finished = _entries.Where(e => e.Execution.Status == TaskStatus.RanToCompletion).Take(MaxPerResponse).ToList();
                foreach (var entry in finished) _entries.Remove(entry);
            }
            if (finished.Count > 0) response["_previousOperation"] = new JArray(finished.Select(Summarize));
        }

        private static JObject Summarize(Entry entry)
        {
            JObject final;
            try { final = JObject.Parse(entry.Execution.Result); } catch { final = new JObject(); }
            var summary = new JObject { ["operationId"] = entry.OperationId, ["tool"] = entry.Tool, ["success"] = final["success"]?.DeepClone() };
            if (final["error"]?.Type == JTokenType.String) summary["error"] = Bounded(final.Value<string>("error"), 256);
            if (final["_operation"] is JObject operation)
            {
                summary["outcome"] = operation["outcome"]?.DeepClone();
                summary["mutationApplied"] = operation["mutationApplied"]?.DeepClone();
            }
            var data = BoundedData(final["data"]);
            if (data != null) summary["data"] = data;
            if (final["_failure"] is JObject failure)
            {
                var brief = new JObject { ["action"] = failure["action"]?.DeepClone(), ["gate"] = failure["gate"]?.DeepClone(),
                    ["messageCount"] = failure["messageCount"]?.DeepClone() };
                if (failure["incidentIds"] is JArray ids) brief["incidentIds"] = new JArray(ids.Take(5));
                if (failure["groups"] is JArray groups)
                    brief["groups"] = new JArray(groups.OfType<JObject>().Take(5).Select(g =>
                    {
                        var group = new JObject();
                        foreach (var key in new[] { "failureDefinitionId", "severity", "count", "title", "answeredBy" })
                            if (g[key] != null) group[key] = g[key].DeepClone();
                        if (g["description"]?.Type == JTokenType.String) group["description"] = Bounded(g.Value<string>("description"), 200);
                        return group;
                    }));
                summary["_failure"] = brief;
            }
            summary["note"] = "Finished after its pending receipt. revit_get_operation_status has the full record.";
            return summary;
        }

        private static JToken BoundedData(JToken data)
        {
            if (data == null || data.Type == JTokenType.Null) return null;
            var bytes = Encoding.UTF8.GetByteCount(data.ToString(Formatting.None));
            return bytes <= MaxDataBytes ? data.DeepClone()
                : new JObject { ["omitted"] = true, ["bytes"] = bytes, ["reason"] = "The command result is larger than this summary allows." };
        }

        private static string Bounded(string value, int max) => value.Length <= max ? value : value.Substring(0, max - 12) + " [truncated]";
    }
}
