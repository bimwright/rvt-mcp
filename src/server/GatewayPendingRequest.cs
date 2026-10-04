using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    internal sealed class GatewayPendingRequest
    {
        internal readonly TaskCompletionSource<string> Caller = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<string> Execution = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string OperationId { get; }
        internal long ConnectionId { get; }
        internal bool WaitingForUser { get; private set; }
        internal RvtMcp.Plugin.ChangeHistoryRequest History { get; set; }
        internal GatewayPendingRequest(string operationId, long connectionId) { OperationId = operationId; ConnectionId = connectionId; }

        // Event routing precedes final handling. An event can return to the caller but never finishes execution.
        internal bool Route(JObject frame)
        {
            if (frame.Value<string>("frame") == "event")
            {
                if (frame.Value<string>("event") == "dialog_wait")
                {
                    WaitingForUser = true;
                    Caller.TrySetResult(new JObject { ["success"] = false, ["error"] = "REVIT_OPERATION_REQUIRES_USER",
                        ["_operation"] = frame["_operation"]?.DeepClone() ?? new JObject {
                            ["operationId"] = OperationId, ["execution"] = "waiting_for_user", ["outcome"] = "unknown", ["complete"] = false }
                    }.ToString(Formatting.None));
                }
                return false;
            }
            var execution = frame["_operation"]?.Value<string>("execution");
            if (execution == "waiting_for_user" || execution == "timed_out_running" || execution == "running" || execution == "queued")
            {
                WaitingForUser = execution == "waiting_for_user";
                Caller.TrySetResult(frame.ToString(Formatting.None));
                return false;
            }
            WaitingForUser = false;
            var json = frame.ToString(Formatting.None);
            Execution.TrySetResult(json);
            Caller.TrySetResult(json);
            return true;
        }

        internal JObject Pending(string execution = "timed_out_running") => new JObject
        {
            ["success"] = false, ["error"] = "REVIT_OPERATION_REQUIRES_USER",
            ["_operation"] = new JObject { ["operationId"] = OperationId, ["execution"] = execution,
                ["outcome"] = "unknown", ["complete"] = false, ["mutationApplied"] = JValue.CreateNull(),
                ["nextStep"] = "Query revit_get_operation_status. Execution has not been confirmed finished; do not retry." }
        };
    }
}
