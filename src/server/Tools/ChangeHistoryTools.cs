using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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
            catch (Exception ex) { HistoryDiagnostics.Report("history_record", ex); return "Error: Local change history is unavailable. No model operation was run; inspect local storage."; }
        }

        [McpServerTool(Name = "revit_get_change_records", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
            "Query local per-model MCP change history by server-issued modelKey, optional elementId or uniqueId, ISO UTC date range and limit (1-100). " +
            "Omit modelKey to query the active saved model; reads do not register identity or create history databases. Confirmed continuation/file aliases include related calls, each with its owning modelKey for reason assignment. " +
            "Matching lineage alone returns needs_choice; ask the owner to continue or separate, then use revit_resolve_history_identity. Set listModels=true to list local keys/titles (including legacy history); paginate with afterModelKey. " +
            "Returns explicit unassigned/unknown reasons, coverage and authoritative before/after when available. " +
            "The public element list is capped at 200 per call; a targeted query can find other stored IDs. " +
            "Deleted UniqueIds may be unavailable. Missing rows do not prove an element was unchanged. " +
            "History describes observed MCP transactions, not whether changes were saved or later manually undone. Available even when recording is off.")]
        public static async Task<string> GetChangeRecords(string modelKey = null, long? elementId = null, string uniqueId = null, string from = null, string until = null, int limit = 50,
            bool listModels = false, string afterModelKey = null)
        {
            try
            {
                if (listModels)
                {
                    if (modelKey != null || elementId != null || uniqueId != null || from != null || until != null)
                        return "Error: listModels cannot be combined with call filters or modelKey.";
                    return Store.ListModels(afterModelKey, limit).ToString(Formatting.None);
                }
                if (afterModelKey != null) return "Error: afterModelKey requires listModels=true.";
                var model = modelKey == null ? await ActiveModel() : null;
                modelKey ??= model.Value<string>("key");
                return Store.Query(modelKey, elementId, uniqueId, from, until, limit, model).ToString(Formatting.None);
            }
            catch (ArgumentException ex) { return "Error: " + ex.Message; }
            catch (Exception ex) { HistoryDiagnostics.Report("history_query", ex); return "Error: Local change history or the active Revit context is unavailable. No model operation was run; check the connection and local storage."; }
        }

        [McpServerTool(Name = "revit_resolve_history_identity", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
            "Record the owner's explicit choice for the active saved model's local history after Save As, a copy or an uncertain path change. " +
            "sourceModelKey must have existing local history (use revit_get_change_records listModels=true). decision is continue or separate; reason records the owner's choice. " +
            "Never infer continuation merely from a shared Revit GUID. Continue links queries across histories; separate keeps this model independent. Calls and their reasons stay in their original databases. " +
            "This can replace an existing logical link; restore the previous grouping with another explicit decision. Cycles and separating proven aliases of the same file are rejected. No Revit model edit or Save/Sync. Disabled in read-only mode or when history recording is off.")]
        public static async Task<string> ResolveHistoryIdentity(string sourceModelKey, string decision, string reason)
        {
            var config = ServerState.Config ?? new RvtMcpConfig();
            if (config.ReadOnlyOrDefault || !config.EnableChangeHistoryOrDefault) return "Error: History identity decisions are disabled in read-only mode or when history recording is off.";
            try { return Store.ResolveIdentity(await ActiveModel(), sourceModelKey, decision, reason).ToString(Formatting.None); }
            catch (ArgumentException ex) { return "Error: " + ex.Message; }
            catch (Exception ex) { HistoryDiagnostics.Report("history_identity_decision", ex); return "Error: History identity decision could not be stored. Query current history before retrying; no model operation was run."; }
        }

        private static async Task<JObject> ActiveModel()
        {
            var context = await ToolGateway.SendToRevit("get_change_records", new { });
            if (!ChangeHistoryStore.IsModelKey(context.Value<string>("modelKey")))
                throw new ArgumentException("The active model has no stable history identity. Open a saved model or supply a previously returned modelKey.");
            var model = new JObject { ["key"] = context["modelKey"] };
            foreach (var property in new[] { "title", "physicalKey", "lineageKey" })
                if (context.Property(property) != null) model[property] = context[property];
            return model;
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
