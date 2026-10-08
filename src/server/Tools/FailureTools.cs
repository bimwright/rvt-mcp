using System;
using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class FailureTools
    {
        internal static Func<FailureLedgerStore> StoreFactory = () => new FailureLedgerStore(readOnly: true);

        [McpServerTool(Name = "revit_get_failure_records", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
            "Read the local failure ledger without dispatching Revit; works while native UI is modal and with history or send-code disabled. " +
            "Supply an issued modelScopeKey, incidentId or operationId; no model is guessed. Filter by state, elementId, ISO UTC from/until, limit 1-100 and returned cursor. " +
            "includeDetails adds captured evidence. Missing records do not certify a model safe. Decisions and rules can only be changed by the user in Revit. " +
            "Example: {operationId: \"<issued-id>\", includeDetails: true}.")]
        public static string GetFailureRecords(string modelScopeKey = null, string incidentId = null, string operationId = null,
            string state = null, long? elementId = null, string from = null, string until = null, int limit = 50, string cursor = null, bool includeDetails = false)
        {
            try
            {
                if (modelScopeKey == null && incidentId == null && operationId == null)
                    throw new ArgumentException("Supply modelScopeKey, incidentId or operationId from a previous receipt; this query never dispatches Revit.");
                ValidateKey(modelScopeKey); ValidateKey(incidentId); ValidateKey(operationId);
                if (limit < 1 || limit > 100) throw new ArgumentException("limit must be between 1 and 100.");
                var query = new FailureRecordQuery { ModelScopeKey = modelScopeKey, IncidentId = incidentId, OperationId = operationId,
                    State = state, ElementId = elementId, From = ParseTime(from), Until = ParseTime(until), Limit = limit, Cursor = cursor, IncludeDetails = includeDetails };
                if (query.From > query.Until) throw new ArgumentException("from must not be after until.");
                return FailureJson.Serialize(StoreFactory().Query(query));
            }
            catch (ArgumentException ex) { return Error("INVALID_ARGUMENT", ex.Message); }
            catch { return Error("FAILURE_STORAGE_FAILED", "Local failure records are unavailable. No Revit operation was dispatched; model safety is unknown."); }
        }

        [McpServerTool(Name = "revit_get_operation_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
            "Read the plugin-owned local status of an issued operationId, including execution, final outcome, incident IDs and bounded receipts. " +
            "Works while Revit is modal; never dispatches, retries or changes Revit. Missing records are unavailable, not proof of failure or permission to retry. " +
            "Example: {operationId: \"<issued-id>\"}.")]
        public static string GetOperationStatus(string operationId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("operationId is required.");
                ValidateKey(operationId);
                var operation = StoreFactory().GetOperation(operationId);
                if (operation == null) return new JObject { ["available"] = false, ["operationId"] = operationId,
                    ["complete"] = false, ["note"] = "No durable operation record is available. Do not infer that execution failed or retry unchanged." }.ToString(Formatting.None);
                var result = JObject.Parse(FailureJson.Serialize(operation));
                if (result["finalResult"] is JObject final)
                {
                    var bounded = new JObject { ["success"] = final["success"], ["error"] = final["error"], ["_changes"] = ChangeSummary.ForAgent(final["_changes"] ?? final["changes"]), ["_history"] = final["_history"] ?? final["history_transfer"] };
                    SafetyResponseMetadata.Attach(bounded, final);
                    result["finalResult"] = bounded;
                }
                result["available"] = true;
                return result.ToString(Formatting.None);
            }
            catch (ArgumentException ex) { return Error("INVALID_ARGUMENT", ex.Message); }
            catch { return Error("FAILURE_STORAGE_FAILED", "Local operation status is unavailable. Do not retry the model operation."); }
        }
        private static string Error(string code, string message) => new JObject { ["success"] = false, ["error"] = code, ["message"] = message }.ToString(Formatting.None);
        private static void ValidateKey(string key)
        {
            if (key == null) return;
            if (!FailureCapture.IsKey(key)) throw new ArgumentException("Invalid identifier; use a bounded issued key, not a file path.");
        }
        private static DateTimeOffset? ParseTime(string value)
        {
            if (value == null) return null;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                throw new ArgumentException("Dates must use ISO date/time format.");
            return time.ToUniversalTime();
        }
    }
}
