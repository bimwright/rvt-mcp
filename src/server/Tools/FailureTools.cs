using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
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

        internal const int MaxWaitSeconds = 90;
        internal static TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(500);

        [McpServerTool(Name = "revit_get_operation_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
            "Read the plugin-owned local status of an issued operationId, including execution, final outcome, incident IDs and bounded receipts; finalResult.data has the command result while this session still holds it. " +
            "Works while Revit is modal; never dispatches, retries or changes Revit. Missing records are unavailable, not proof of failure or permission to retry. " +
            "waitSeconds (0-90, default 0) waits here, without holding Revit, while the operation is unfinished (waiting_for_user, timed_out_running, running, queued); " +
            "it returns as soon as the operation finishes, otherwise with stillWaiting=true. " +
            "Example: {operationId: \"<issued-id>\", waitSeconds: 60}.")]
        public static async Task<string> GetOperationStatus(string operationId, int waitSeconds = 0)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("operationId is required.");
                ValidateKey(operationId);
                if (waitSeconds < 0 || waitSeconds > MaxWaitSeconds)
                    throw new ArgumentException($"waitSeconds must be between 0 and {MaxWaitSeconds} (got {waitSeconds}). The unit is seconds.");
                var clock = Stopwatch.StartNew();
                var limit = TimeSpan.FromSeconds(waitSeconds);
                while (true)
                {
                    var status = ReadStatus(operationId);
                    var unfinished = Unfinished(status);
                    var remaining = limit - clock.Elapsed;
                    if (!unfinished || remaining <= TimeSpan.Zero)
                    {
                        if (waitSeconds > 0)
                        {
                            status["waitedSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
                            if (unfinished) status["stillWaiting"] = true;
                        }
                        return status.ToString(Formatting.None);
                    }
                    // The session's own request wakes the wait as soon as its result arrives; otherwise only the ledger can tell.
                    var execution = ToolGateway.PreviousOperations.ExecutionOf(operationId);
                    if (execution != null && !execution.IsCompleted) await Task.WhenAny(execution, Task.Delay(remaining));
                    else await Task.Delay(remaining < WaitPollInterval ? remaining : WaitPollInterval);
                }
            }
            catch (ArgumentException ex) { return Error("INVALID_ARGUMENT", ex.Message); }
            catch { return Error("FAILURE_STORAGE_FAILED", "Local operation status is unavailable. Do not retry the model operation."); }
        }

        private static bool Unfinished(JObject status)
        {
            if (status.Value<bool?>("available") != true) return false;
            var execution = status.Value<string>("execution");
            return execution == "waiting_for_user" || execution == "timed_out_running" || execution == "running" || execution == "queued";
        }

        private static JObject ReadStatus(string operationId)
        {
            var operation = StoreFactory().GetOperation(operationId);
            if (operation == null)
                // Reads have no durable record; this session may still hold the request.
                return ToolGateway.PreviousOperations.Status(operationId) is JObject session
                    ? MarkReadIfCompleted(operationId, session)
                    : new JObject { ["available"] = false, ["operationId"] = operationId,
                        ["complete"] = false, ["note"] = "No durable operation record is available. Do not infer that execution failed or retry unchanged." };
            var result = JObject.Parse(FailureJson.Serialize(operation));
            if (result["finalResult"] is JObject final)
            {
                var bounded = new JObject { ["success"] = final["success"], ["error"] = final["error"], ["_changes"] = ChangeSummary.ForAgent(final["_changes"] ?? final["changes"]), ["_history"] = final["_history"] ?? final["history_transfer"] };
                // The ledger keeps receipts only; the command's own result is available while this session holds it.
                var data = operation.Execution == "completed" ? ToolGateway.PreviousOperations.FinalData(operationId) : null;
                if (data != null) bounded["data"] = data;
                SafetyResponseMetadata.Attach(bounded, final);
                if (operation.Execution == "completed")
                    ToolGateway.CarryOnce(ToolGateway.PreviousOperations.Status(operationId)?["finalResult"] as JObject, bounded);
                result["finalResult"] = bounded;
            }
            result["available"] = true;
            return MarkReadIfCompleted(operationId, result);
        }

        private static JObject MarkReadIfCompleted(string operationId, JObject status)
        {
            if (status.Value<string>("execution") == "completed") ToolGateway.PreviousOperations.MarkRead(operationId);
            return status;
        }
        [McpServerTool(Name = "revit_lookup_message", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
            "Explain a Revit dialog or failure code from the catalogue of the bound Revit version. Reads a local file and never dispatches Revit, so it works while Revit shows a dialog. " +
            "Pass a dialog code (_operation.dialog.code, blockedBy.dialog.code) or a failure GUID (failureDefinitionId). Returns the dialog's title, template text and buttons, " +
            "or the failure's severity, description and resolutions. The add-in builds the catalogue from the Revit installed on this machine; " +
            "add-in dialogs, Windows dialogs and dialogs without a code are not in it. Example: {code: \"<code from a receipt>\"}.")]
        public static string LookupMessage(string code)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(code) || code.Length > 256)
                    throw new ArgumentException("code is required: a dialog code or failure GUID taken from a receipt.");
                var year = YearProvider();
                if (year == null)
                    return Error("TARGET_NOT_BOUND", "No Revit target is bound yet. Call revit_get_current_target or another Revit tool first; this lookup never dispatches Revit.");
                return RevitMessageLookup.Lookup(Catalog(year.Value), code.Trim(), year.Value).ToString(Formatting.None);
            }
            catch (ArgumentException ex) { return Error("INVALID_ARGUMENT", ex.Message); }
            catch { return Error("CATALOG_UNAVAILABLE", "The local message catalogue could not be read. No Revit operation was dispatched."); }
        }

        internal static Func<int?> YearProvider = () => int.TryParse(ToolGateway.CurrentRevitVersion, out var year) ? year : (int?)null;
        private static readonly object CatalogGate = new object();
        private static string _catalogPath;
        private static DateTime _catalogStamp;
        private static RevitMessageCatalogData _catalog;

        // The add-in rewrites the file when Revit's build or language changes.
        private static RevitMessageCatalogData Catalog(int year)
        {
            var path = RevitMessageLookup.CachePath(year);
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (CatalogGate)
            {
                if (path != _catalogPath || stamp != _catalogStamp)
                {
                    _catalog = stamp == DateTime.MinValue ? null : RevitMessageLookup.Read(path);
                    _catalogPath = path;
                    _catalogStamp = stamp;
                }
                return _catalog;
            }
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
