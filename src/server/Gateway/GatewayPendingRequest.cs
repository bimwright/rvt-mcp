using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Scripting;

namespace RvtMcp.Server
{
    internal sealed class GatewayPendingRequest
    {
        internal readonly TaskCompletionSource<string> Caller = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<string> Execution = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string OperationId { get; }
        internal long ConnectionId { get; }
        private volatile bool _waitingForUser;
        internal bool WaitingForUser => _waitingForUser;
        private JObject _dialog;
        /// <summary>The dialog this request waits on, as the add-in reported it; null when unknown.</summary>
        internal JObject Dialog { get { lock (_gate) return (JObject)_dialog?.DeepClone(); } }
        internal RvtMcp.Plugin.ChangeHistoryRequest History { get; }
        internal JObject HistoryReceipt { get; private set; }
        private readonly object _gate = new object();
        private readonly IDisposable _historyLease;
        private bool _finished;
        private readonly JObject _script;
        private readonly Action<JObject> _finalizeScript;
        internal GatewayPendingRequest(string operationId, long connectionId, RvtMcp.Plugin.ChangeHistoryRequest history = null,
            JObject script = null, Action<JObject> finalizeScript = null)
        {
            OperationId = operationId;
            ConnectionId = connectionId;
            History = history;
            _historyLease = history == null ? null : new Memory.HistoryCallLease(history.Id);
            _script = script == null ? null : (JObject)script.DeepClone();
            _finalizeScript = finalizeScript;
        }

        // Event routing precedes final handling. An event can return to the caller but never finishes execution.
        internal bool Route(JObject frame)
        {
            lock (_gate)
            {
                if (_finished) return true;
                // Catalog handshakes are control-plane requests, not model operations.
                if (OperationId != null)
                {
                    if (frame.Value<string>("frame") == "event")
                    {
                        if (frame.Value<string>("event") == "dialog_wait")
                        {
                            _waitingForUser = true;
                            var receipt = Pending("waiting_for_user");
                            if (frame["_operation"] != null) receipt["_operation"] = frame["_operation"].DeepClone();
                            var dialog = receipt["_operation"]?["dialog"] as JObject ?? frame["payload"]?["dialog"] as JObject;
                            if (dialog != null)
                            {
                                _dialog = (JObject)dialog.DeepClone();
                                if (receipt["_operation"] is JObject operation) operation["dialog"] = dialog.DeepClone();
                            }
                            Caller.TrySetResult(receipt.ToString(Formatting.None));
                        }
                        return false;
                    }
                    var execution = frame["_operation"]?.Value<string>("execution");
                    if (execution == "waiting_for_user" || execution == "timed_out_running" || execution == "running" || execution == "queued")
                    {
                        if (_script != null && frame["_script"] == null) ScriptTelemetry.AttachPending(frame, _script);
                        _waitingForUser = execution == "waiting_for_user";
                        Caller.TrySetResult(frame.ToString(Formatting.None));
                        return false;
                    }
                }
                _waitingForUser = false;
                _finished = true;
                if (_script != null)
                {
                    if (frame["_script"] == null) ScriptTelemetry.AttachPending(frame, _script);
                    // The gateway retains its own identity even when the host compacted that part of the wire receipt.
                    foreach (var property in _script.Properties())
                        if (property.Name != "script_stage" && property.Name != "compile_attempted" && property.Name != "invoke_started"
                            && frame["_script"][property.Name] == null)
                            frame["_script"][property.Name] = property.Value.DeepClone();
                    frame["_script"]["wire_request_bytes"] = _script["wire_request_bytes"]?.DeepClone();
                    try { _finalizeScript?.Invoke(frame); }
                    catch { frame["_script"]["telemetry_status"] = "write_failed"; }
                }
                try { HistoryReceipt = ToolGateway.CaptureHistory(frame, History, recordContext: false); }
                finally { _historyLease?.Dispose(); }
                var json = frame.ToString(Formatting.None);
                Execution.TrySetResult(json);
                Caller.TrySetResult(json);
                return true;
            }
        }

        internal void Interrupt(Exception error)
        {
            lock (_gate)
            {
                if (_finished) return;
                _finished = true;
                // A pending receipt already returned to the caller must not be replaced.
                // The native ledger remains authoritative after losing this connection.
                Caller.TrySetException(error);
                Execution.TrySetCanceled();
                _historyLease?.Dispose();
            }
        }

        internal bool Timeout() => Caller.TrySetResult(Pending().ToString(Formatting.None));

        internal static JObject NotStarted(string operationId) => new JObject
        {
            ["operationId"] = operationId, ["execution"] = "completed", ["outcome"] = "not_started",
            ["mutationApplied"] = false, ["complete"] = true
        };

        internal JObject Pending(string execution = "timed_out_running")
        {
            var result = new JObject {
            ["success"] = false, ["error"] = execution == "waiting_for_user" ? "REVIT_OPERATION_REQUIRES_USER" : "REVIT_OPERATION_TIMED_OUT",
            ["_operation"] = new JObject { ["operationId"] = OperationId, ["execution"] = execution,
                ["outcome"] = "unknown", ["complete"] = false, ["mutationApplied"] = JValue.CreateNull(),
                ["nextStep"] = execution == "waiting_for_user" ? RvtMcp.Failures.FailureCapture.WaitingNextStep
                    : execution == "timed_out_running" ? RvtMcp.Failures.FailureCapture.TimedOutNextStep
                    : "Query revit_get_operation_status. Execution has not been confirmed finished; do not retry." }
            };
            ScriptTelemetry.AttachPending(result, _script);
            return result;
        }

        /// <summary>What a refused call reports about the request that holds Revit.</summary>
        internal JObject BlockedBy()
        {
            var blockedBy = new JObject { ["operationId"] = OperationId };
            var dialog = Dialog;
            if (dialog != null) blockedBy["dialog"] = dialog;
            return blockedBy;
        }
    }
}
