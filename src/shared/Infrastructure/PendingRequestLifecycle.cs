using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Caller delivery and Revit execution are separate lifetimes. No UI callback writes a socket.</summary>
    public sealed class PendingRequestLifecycle
    {
        private static readonly ConditionalWeakTable<TaskCompletionSource<string>, PendingRequestLifecycle> Requests = new ConditionalWeakTable<TaskCompletionSource<string>, PendingRequestLifecycle>();
        private static readonly string HostId = ProcessIdentity();
        private static int _modalCount;
        private static int _hostBusy;
        private static string _hostBusyMetadata;
        private readonly object _sync = new object();
        private readonly TaskCompletionSource<string> _execution;
        private readonly TaskCompletionSource<string> _caller = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state; // 0 queued, 1 running, 2 cancelled before start, 3 completed
        private bool _waiting;
        private Action<string> _emit;
        public string RequestId { get; private set; }
        public string OperationId { get; private set; } = Guid.NewGuid().ToString("N");
        public string HostInstanceId => HostId;
        public static bool IsHostWaitingForUser => Volatile.Read(ref _modalCount) != 0 || Volatile.Read(ref _hostBusy) != 0;
        public static void SetHostBusy(bool busy, JObject safePayload = null)
        {
            // Retain only issued identifiers and the explicitly qualified correlation; never unscoped dialog contents.
            var metadata = new JObject();
            if (busy && safePayload != null)
            {
                if (safePayload.Value<string>("correlation") == "correlated_not_proven")
                    metadata["correlation"] = "correlated_not_proven";
                foreach (var key in new[] { "operationId", "modelScopeKey" })
                {
                    var value = safePayload.Value<string>(key);
                    if (value != null && value.Length <= 128 && System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]+$"))
                        metadata[key] = value;
                }
            }
            Volatile.Write(ref _hostBusyMetadata, metadata.Count == 0 ? null : metadata.ToString(Formatting.None));
            Interlocked.Exchange(ref _hostBusy, busy ? 1 : 0);
        }
        public static JObject HostBusyMetadata
        {
            get { var json = Volatile.Read(ref _hostBusyMetadata); return json == null ? null : JObject.Parse(json); }
        }
        private static string ProcessIdentity()
        {
            using (var process = Process.GetCurrentProcess())
            {
                try { return process.Id + "-" + process.StartTime.ToUniversalTime().Ticks; }
                catch { return process.Id + "-unknown"; }
            }
        }
        public bool HasStarted { get { lock (_sync) return _state == 1 || _state == 3; } }
        public Task<string> CallerTask => _caller.Task;

        private PendingRequestLifecycle(TaskCompletionSource<string> execution)
        {
            _execution = execution;
            execution.Task.ContinueWith(task =>
            {
                lock (_sync)
                {
                    _state = 3;
                    if (_waiting) { _waiting = false; Interlocked.Decrement(ref _modalCount); }
                    if (task.Status == TaskStatus.RanToCompletion) _caller.TrySetResult(task.Result);
                    else if (task.IsCanceled) _caller.TrySetCanceled();
                    else _caller.TrySetException(task.Exception.InnerExceptions);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public static PendingRequestLifecycle For(TaskCompletionSource<string> execution) => Requests.GetValue(execution, key => new PendingRequestLifecycle(key));

        public void Configure(string requestId, string operationId, Action<string> emit)
        {
            lock (_sync)
            {
                RequestId = requestId;
                if (Guid.TryParse(operationId, out var parsed)) OperationId = parsed.ToString("N");
                _emit = emit;
            }
        }

        public bool TryStart()
        {
            lock (_sync)
            {
                if (_state != 0 || _execution.Task.IsCompleted) return false;
                _state = 1;
                return true;
            }
        }

        public void EmitDialogWait(JObject safePayload)
        {
            lock (_sync)
            {
                if (_state != 1) return;
                if (!_waiting) { _waiting = true; Interlocked.Increment(ref _modalCount); }
                var pending = Pending("REVIT_OPERATION_REQUIRES_USER", "waiting_for_user", "unknown");
                var frame = new JObject
                {
                    ["frame"] = "event", ["event"] = "dialog_wait", ["id"] = RequestId,
                    ["operationId"] = OperationId, ["hostInstanceId"] = HostInstanceId,
                    ["payload"] = safePayload?.DeepClone(), ["_operation"] = pending["_operation"]?.DeepClone()
                };
                // The transport callback only enqueues immutable JSON; it never waits for the wire.
                _emit?.Invoke(frame.ToString(Formatting.None));
                _caller.TrySetResult(pending.ToString(Formatting.None));
            }
        }

        public string Timeout()
        {
            lock (_sync)
            {
                if (_execution.Task.Status == TaskStatus.RanToCompletion) return _execution.Task.Result;
                var notStarted = _state == 0;
                if (notStarted) _state = 2;
                var response = Pending(notStarted ? "REVIT_REQUEST_NOT_STARTED" : "REVIT_OPERATION_REQUIRES_USER",
                    notStarted ? "completed" : _waiting ? "waiting_for_user" : "timed_out_running",
                    notStarted ? "not_started" : "unknown");
                response["message"] = "Request timed out. Do not retry; query this operation ID before another model operation.";
                var json = response.ToString(Formatting.None);
                _caller.TrySetResult(json);
                if (notStarted) _execution.TrySetResult(json);
                return json;
            }
        }

        public JObject Pending(string error, string execution, string outcome) => new JObject
        {
            ["id"] = RequestId, ["success"] = false, ["error"] = error,
            ["_operation"] = new JObject
            {
                ["schemaVersion"] = 1, ["operationId"] = OperationId, ["hostInstanceId"] = HostInstanceId,
                ["execution"] = execution, ["outcome"] = outcome,
                ["mutationApplied"] = outcome == "not_started" ? new JValue(false) : JValue.CreateNull(),
                ["complete"] = outcome == "not_started",
                ["nextStep"] = "Query revit_get_operation_status with this operationId. Do not retry unchanged."
            }
        };
    }
}
