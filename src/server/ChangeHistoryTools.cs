using System;
using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server.Memory;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class ChangeHistoryTools
    {
        [McpServerTool(Name = "revit_record_change", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
            "Attach the user's reason to explicit server-issued change callIds in one model's local history. " +
            "Use modelKey and callId from _history receipts; callIds is a JSON string array. Unknown or already assigned calls are rejected atomically. " +
            "This writes local history only, not the Revit model. Empty reason is recorded as unknown; never invent a reason. " +
            "Before/after and changed element IDs come from captured tool execution, not this tool's arguments. " +
            "Optional alternativesJson, surveyJson and remainingWorkJson are JSON arrays/objects. Requires change history enabled.")]
        public static string RecordChange(string modelKey, string callIds, string requestText, string goal, string reason = null,
            string selectedOption = null, string alternativesJson = null, string surveyJson = null, string remainingWorkJson = null)
        {
            var config = ServerState.Config ?? new RvtMcpConfig();
            if (config.ReadOnlyOrDefault) return "Error: Recording a reason is disabled in read-only mode.";
            if (!config.EnableChangeHistoryOrDefault) return "Error: Change history recording is disabled.";
            try
            {
                var ids = JArray.Parse(callIds);
                if (ids.Any(x => x.Type != JTokenType.String)) return "Error: callIds must be a JSON string array.";
                var context = new JObject { ["request"] = requestText, ["goal"] = goal, ["reason"] = reason, ["selectedOption"] = selectedOption,
                    ["alternatives"] = Structured(alternativesJson), ["survey"] = Structured(surveyJson), ["remainingWork"] = Structured(remainingWorkJson) };
                return Store.Record(modelKey, ids.Values<string>().ToArray(), context).ToString(Formatting.None);
            }
            catch (ArgumentException ex) { return "Error: " + ex.Message; }
            catch (JsonException) { return "Error: Invalid JSON in change record arguments."; }
            catch { return "Error: Local change history is unavailable. No model operation was run; inspect local storage."; }
        }

        [McpServerTool(Name = "revit_get_change_records", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
            "Query local per-model MCP change history by server-issued modelKey, optional elementId or uniqueId, ISO UTC date range and limit (1-100). " +
            "Use modelKey from a recent _history receipt. Returns explicit unassigned/unknown reasons, coverage and authoritative before/after when available. " +
            "The public element list is capped at 200 per call; a targeted query can find other stored IDs. " +
            "Deleted UniqueIds may be unavailable. Missing rows do not prove an element was unchanged. " +
            "History describes observed MCP transactions, not whether changes were saved or later manually undone. Available even when recording is off.")]
        public static string GetChangeRecords(string modelKey, long? elementId = null, string uniqueId = null, string from = null, string until = null, int limit = 50)
        {
            try { return Store.Query(modelKey, elementId, uniqueId, from, until, limit).ToString(Formatting.None); }
            catch (ArgumentException ex) { return "Error: " + ex.Message; }
            catch { return "Error: Local change history is unavailable. No model operation was run; inspect local storage."; }
        }
        private static ChangeHistoryStore Store => ToolGateway.History ??= new ChangeHistoryStore();
        private static JToken Structured(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var value = JToken.Parse(json);
            if (!(value is JArray) && !(value is JObject)) throw new ArgumentException("Structured reason context must be a JSON object or array.");
            return value;
        }
    }
}
