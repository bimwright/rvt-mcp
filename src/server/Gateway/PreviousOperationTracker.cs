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
            internal string OperationId, Tool, PendingExecution;
            internal bool IsRead, Delivered;
            internal Task<string> Execution;
        }

        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();

        internal void Remember(string operationId, string tool, Task<string> execution, string pendingExecution = "running", bool isRead = false)
        {
            if (string.IsNullOrEmpty(operationId) || execution == null) return;
            lock (_gate)
            {
                if (_entries.Any(e => e.OperationId == operationId)) return;
                if (_entries.Count >= MaxTracked) _entries.Remove(_entries.FirstOrDefault(e => e.Delivered) ?? _entries[0]);
                _entries.Add(new Entry { OperationId = operationId, Tool = tool, Execution = execution, PendingExecution = pendingExecution, IsRead = isRead });
            }
        }

        /// <summary>The agent read the final status itself. A result the server has not received yet still follows later.</summary>
        internal void MarkRead(string operationId)
        {
            lock (_gate)
                foreach (var entry in _entries.Where(e => e.OperationId == operationId && e.Execution.IsCompleted)) entry.Delivered = true;
        }

        /// <summary>The native execution this session still waits on or holds, or null.</summary>
        internal Task<string> ExecutionOf(string operationId)
        {
            lock (_gate) return _entries.FirstOrDefault(e => e.OperationId == operationId)?.Execution;
        }

        /// <summary>
        /// The command's own result, bounded, while this session still holds it. The ledger keeps receipts only,
        /// never a command result, so this is the one place a status query can find it.
        /// </summary>
        internal JToken FinalData(string operationId)
        {
            Entry entry;
            lock (_gate) entry = _entries.FirstOrDefault(e => e.OperationId == operationId);
            if (entry == null || entry.Execution.Status != TaskStatus.RanToCompletion) return null;
            try { return BoundedData(JObject.Parse(entry.Execution.Result)["data"], entry.IsRead); } catch { return null; }
        }

        /// <summary>
        /// Status of an operation the ledger has no record of: reads are not recorded there. Only this session knows it,
        /// so it is lost when the server restarts. Null when this session holds nothing for the ID.
        /// </summary>
        internal JObject Status(string operationId)
        {
            Entry entry;
            lock (_gate) entry = _entries.FirstOrDefault(e => e.OperationId == operationId);
            if (entry == null) return null;
            var status = new JObject { ["available"] = true, ["source"] = "gateway_session", ["operationId"] = operationId, ["tool"] = entry.Tool };
            var execution = entry.Execution;
            if (execution.Status != TaskStatus.RanToCompletion)
            {
                // A lost connection ends the wait without a result.
                status["execution"] = execution.IsCompleted ? "interrupted_unknown" : entry.PendingExecution;
                status["complete"] = false;
                status["note"] = "No durable record: this server session tracks the request until its result arrives. Do not retry unchanged.";
                return status;
            }
            JObject final;
            try { final = JObject.Parse(execution.Result); } catch { final = new JObject(); }
            status["execution"] = "completed";
            if (final["_operation"] is JObject operation)
                foreach (var key in new[] { "outcome", "mutationApplied", "complete" })
                    if (operation[key] != null) status[key] = operation[key].DeepClone();
            if (status["complete"] == null) status["complete"] = true;
            var bounded = new JObject { ["success"] = final["success"]?.DeepClone(), ["error"] = final["error"]?.DeepClone() };
            var data = BoundedData(final["data"], entry.IsRead);
            if (data != null) bounded["data"] = data;
            RvtMcp.Plugin.SafetyResponseMetadata.Attach(bounded, final);
            status["finalResult"] = bounded;
            status["note"] = "From this server session's memory; read commands have no durable record.";
            return status;
        }

        internal void Clear()
        {
            lock (_gate) _entries.Clear();
        }

        /// <summary>Adds finished results not yet delivered and returns their IDs. <see cref="Return"/> undoes it when the response cannot carry them.</summary>
        internal IReadOnlyList<string> Attach(JObject response)
        {
            if (response == null) return new string[0];
            List<Entry> finished;
            lock (_gate)
            {
                // A lost connection ends the wait without a result; the local ledger still answers status queries.
                _entries.RemoveAll(e => e.Execution.IsCanceled || e.Execution.IsFaulted);
                finished = _entries.Where(e => !e.Delivered && e.Execution.Status == TaskStatus.RanToCompletion).Take(MaxPerResponse).ToList();
                // Delivered results stay for status queries until newer operations push them out.
                foreach (var entry in finished) entry.Delivered = true;
            }
            if (finished.Count > 0) response["_previousOperation"] = new JArray(finished.Select(Summarize));
            return finished.Select(e => e.OperationId).ToList();
        }

        /// <summary>The response did not reach the agent with these results: the next response carries them.</summary>
        internal void Return(IEnumerable<string> operationIds)
        {
            lock (_gate)
                foreach (var entry in _entries.Where(e => operationIds.Contains(e.OperationId))) entry.Delivered = false;
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
            var data = BoundedData(final["data"], entry.IsRead);
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
                        foreach (var key in new[] { "failureDefinitionId", "severity", "count", "title", "answeredBy", "answeredCount" })
                            if (g[key] != null) group[key] = g[key].DeepClone();
                        if (g["description"]?.Type == JTokenType.String) group["description"] = Bounded(g.Value<string>("description"), 200);
                        return group;
                    }));
                summary["_failure"] = brief;
            }
            // The add-in let go of these when the operation finished; this summary may be the only place the agent sees them.
            // They stay inside this entry: they belong to the model this operation ran on, not to the current call.
            if (final["_userDialogs"] is JObject userDialogs)
                summary["_userDialogs"] = Encoding.UTF8.GetByteCount(userDialogs.ToString(Formatting.None)) <= MaxDataBytes ? userDialogs.DeepClone()
                    : new JObject { ["model"] = userDialogs["model"]?.DeepClone(), ["omitted"] = true,
                        ["reason"] = "Dialogs the user handled were too large to report." };
            summary["note"] = "Finished after its pending receipt. revit_get_operation_status has the full record.";
            return summary;
        }

        private static JToken BoundedData(JToken data, bool isRead)
        {
            if (data == null || data.Type == JTokenType.Null) return null;
            var bytes = Encoding.UTF8.GetByteCount(data.ToString(Formatting.None));
            if (bytes <= MaxDataBytes) return data.DeepClone();
            // Repeating a read is harmless; repeating a write is not, so only a read gets this hint.
            var reason = "The command result is larger than this summary allows." + (isRead ? " Re-run the read with a narrower request." : "");
            return new JObject { ["omitted"] = true, ["bytes"] = bytes, ["reason"] = reason };
        }

        private static string Bounded(string value, int max) => value.Length <= max ? value : value.Substring(0, max - 12) + " [truncated]";
    }
}
