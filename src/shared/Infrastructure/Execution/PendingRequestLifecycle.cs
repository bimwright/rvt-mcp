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
        private string _waitingWindow;
        private bool _waiting;
        private Action<string> _emit;
        public string RequestId { get; private set; }
        public string OperationId { get; private set; } = Guid.NewGuid().ToString("N");
        public string HostInstanceId => HostId;
        public static bool IsHostWaitingForUser => Volatile.Read(ref _modalCount) != 0 || Volatile.Read(ref _hostBusy) != 0 || WindowModal != null;

        // A window without a Revit event, confirmed by the window probe outside any MCP command. Owned by the probe and
        // short-lived: it expires unless the probe refreshes it, so a stopped or failed probe can never leave Revit "busy".
        private sealed class WindowModalState { internal string AppearanceId, Title; internal long ExpiresTicks; }
        private static WindowModalState _windowModal;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<PendingRequestLifecycle, byte> Queued
            = new System.Collections.Concurrent.ConcurrentDictionary<PendingRequestLifecycle, byte>();
        internal static readonly TimeSpan WindowModalLifetime = TimeSpan.FromMilliseconds(1500);
        internal static Func<long> Clock = () => Stopwatch.GetTimestamp();

        /// <summary>Sets or refreshes the probe's window state. Only the title of the window is ever kept (owner decision).</summary>
        public static void SetWindowModal(string appearanceId, string title)
        {
            if (string.IsNullOrEmpty(appearanceId)) return;
            Volatile.Write(ref _windowModal, new WindowModalState { AppearanceId = appearanceId,
                Title = RvtMcp.Failures.FailureCapture.SafeText(title ?? "", 256),
                ExpiresTicks = Clock() + (long)(WindowModalLifetime.TotalSeconds * Stopwatch.Frequency) });
        }

        /// <summary>Clears the window state only if it still belongs to this appearance; the event flag is never touched.</summary>
        public static void ClearWindowModal(string appearanceId)
        {
            var current = Volatile.Read(ref _windowModal);
            if (current != null && current.AppearanceId == appearanceId) Interlocked.CompareExchange(ref _windowModal, null, current);
        }

        /// <summary>The unexpired window shown outside MCP, as safe metadata (kind and title only), or null.</summary>
        public static JObject WindowModal
        {
            get
            {
                var current = Volatile.Read(ref _windowModal);
                if (current == null || Clock() > current.ExpiresTicks) return null;
                return new JObject { ["kind"] = "window", ["title"] = current.Title };
            }
        }

        /// <summary>Answers every request still queued: Revit cannot start it while the window waits. Returns how many were answered.</summary>
        public static int FailQueued()
        {
            int answered = 0;
            foreach (var lifecycle in Queued.Keys)
                if (lifecycle.NotStartedNow()) answered++;
            return answered;
        }

        private static void AttachWindow(JObject response)
        {
            var window = WindowModal;
            if (window == null || !(response["_operation"] is JObject operation)) return;
            operation["blockedByWindow"] = true;
            operation["window"] = window;
            operation["nextStep"] = RvtMcp.Failures.FailureCapture.WindowBlockedNextStep;
        }
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
                    Queued.TryRemove(this, out _);
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
                if (_state == 0 && !_execution.Task.IsCompleted) Queued.TryAdd(this, 0);
            }
        }

        public bool TryStart()
        {
            lock (_sync)
            {
                Queued.TryRemove(this, out _);
                if (_state != 0 || _execution.Task.IsCompleted) return false;
                _state = 1;
                return true;
            }
        }

        // Same outcome as a timeout before start, delivered as soon as a blocking window is confirmed.
        private bool NotStartedNow()
        {
            lock (_sync)
            {
                if (_state != 0 || _execution.Task.IsCompleted) { Queued.TryRemove(this, out _); return false; }
            }
            Timeout();
            return true;
        }

        public void EmitDialogWait(JObject safePayload)
        {
            lock (_sync)
            {
                if (_state != 1) return;
                _waitingWindow = safePayload?["dialog"]?.Value<string>("appearanceId");
                if (!_waiting) { _waiting = true; Interlocked.Increment(ref _modalCount); }
                var pending = Pending("REVIT_OPERATION_REQUIRES_USER", "waiting_for_user", "unknown");
                if (safePayload?["dialog"] is JObject dialog && pending["_operation"] is JObject operation)
                {
                    operation["dialog"] = dialog.DeepClone();
                    if (dialog.Value<string>("kind") == "window") operation["nextStep"] = RvtMcp.Failures.FailureCapture.WindowWaitingNextStep;
                }
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

        public void WindowClosed(string appearanceId)
        {
            lock (_sync)
            {
                if (_state != 1 || !_waiting || appearanceId == null || appearanceId != _waitingWindow) return;
                _waitingWindow = null;
                _waiting = false;
                Interlocked.Decrement(ref _modalCount);
                _emit?.Invoke(new JObject
                {
                    ["frame"] = "event", ["event"] = "window_closed", ["id"] = RequestId, ["appearanceId"] = appearanceId
                }.ToString(Formatting.None));
            }
        }

        public string Timeout()
        {
            lock (_sync)
            {
                if (_execution.Task.Status == TaskStatus.RanToCompletion) return _execution.Task.Result;
                var notStarted = _state == 0;
                if (notStarted) { _state = 2; Queued.TryRemove(this, out _); }
                var response = Pending(notStarted ? "REVIT_REQUEST_NOT_STARTED" : _waiting ? "REVIT_OPERATION_REQUIRES_USER" : "REVIT_OPERATION_TIMED_OUT",
                    notStarted ? "completed" : _waiting ? "waiting_for_user" : "timed_out_running",
                    notStarted ? "not_started" : "unknown");
                response["message"] = notStarted ? "Revit did not start this request before the timeout because it was busy; nothing ran."
                    : "Request timed out. Do not retry; query this operation ID before another model operation.";
                if (notStarted) AttachWindow(response);
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
                ["nextStep"] = execution == "waiting_for_user" ? RvtMcp.Failures.FailureCapture.WaitingNextStep
                    : outcome == "not_started" ? RvtMcp.Failures.FailureCapture.NotStartedNextStep
                    : RvtMcp.Failures.FailureCapture.TimedOutNextStep
            }
        };
    }
}
