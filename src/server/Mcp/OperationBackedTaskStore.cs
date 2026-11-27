using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    /// <summary>
    /// Carries the active MCP task identity and host-dispatch state across the background task execution
    /// created by <c>WithTasks</c> (SEP-2663), binding <c>taskId</c> 1:1 to <c>operationId</c>.
    /// </summary>
    internal sealed class RevitTaskContext
    {
        private static readonly AsyncLocal<RevitTaskContext> Slot = new AsyncLocal<RevitTaskContext>();

        internal static RevitTaskContext Current
        {
            get => Slot.Value;
            set => Slot.Value = value;
        }

        internal string TaskId { get; }
        internal OperationBackedTaskStore Store { get; }
        internal DateTimeOffset DeadlineUtc { get; }
        internal volatile bool DispatchedToHost;

        internal RevitTaskContext(string taskId, OperationBackedTaskStore store, DateTimeOffset deadlineUtc)
        {
            TaskId = taskId;
            Store = store;
            DeadlineUtc = deadlineUtc;
        }

        internal bool TryMarkDispatched(string command, CancellationToken ct)
        {
            if (!Store.TryMarkDispatched(TaskId, command, ct))
                return false;
            DispatchedToHost = true;
            return true;
        }
    }

    /// <summary>
    /// <see cref="IMcpTaskStore"/> backed by <see cref="PreviousOperationTracker"/> and
    /// <see cref="FailureLedgerStore"/> (BW-MCP-UPGRADE-001: TASK-ON-OPERATION, TASK-CANCEL-HONEST,
    /// TASK-RECOVER, TASK-TIMING).
    /// </summary>
    internal sealed class OperationBackedTaskStore : IMcpTaskStore
    {
        // TASK-TIMING: Claude Code recommends keeping task results >= 1h and polling 10-30s for >1m work.
        internal static readonly TimeSpan RecommendedTimeToLive = TimeSpan.FromHours(1);
        internal const long RecommendedPollIntervalMs = 10_000;

        private static readonly long SweepIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

        private sealed class TaskRecord
        {
            public readonly object Sync = new object();
            public McpTaskInfo Info;
            public bool DispatchedToHost;
            public bool CancelRequestedAfterDispatch;
            public bool IsReadOnlyCommand;
        }

        private readonly ConcurrentDictionary<string, TaskRecord> _tasks = new ConcurrentDictionary<string, TaskRecord>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTimeOffset> _cancelledRecoveredTasks = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        private readonly TimeSpan? _defaultTimeToLive;
        private readonly long _defaultPollIntervalMs;
        private readonly Func<FailureLedgerStore> _ledgerFactory;
        private readonly Func<string, JObject> _sessionStatusLookup;
        private readonly Func<DateTimeOffset> _utcNow;
        private long _lastSweepTicks;

        public event Action<InputResponseReceivedEventArgs> InputResponseReceived;

        internal OperationBackedTaskStore(
            TimeSpan? timeToLive = null,
            long pollIntervalMs = RecommendedPollIntervalMs,
            Func<FailureLedgerStore> ledgerFactory = null,
            Func<string, JObject> sessionStatusLookup = null,
            Func<DateTimeOffset> utcNow = null)
        {
            if (pollIntervalMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(pollIntervalMs), "Poll interval must be positive.");
            _defaultTimeToLive = timeToLive ?? RecommendedTimeToLive;
            _defaultPollIntervalMs = pollIntervalMs;
            _ledgerFactory = ledgerFactory ?? (() => FailureTools.StoreFactory());
            _sessionStatusLookup = sessionStatusLookup ?? (id => ToolGateway.PreviousOperations.Status(id));
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _lastSweepTicks = _utcNow().UtcTicks;
        }

        // Must stay synchronous (no async/await) so setting RevitTaskContext.Current mutates the caller's
        // ExecutionContext in RunAsTaskAsync before Task.Run captures it for ExecuteTaskAsync.
        public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _utcNow();
            SweepExpired(now);

            var taskId = Guid.NewGuid().ToString("N");
            var info = new McpTaskInfo(
                taskId,
                McpTaskStatus.Working,
                now,
                now,
                _defaultTimeToLive,
                _defaultPollIntervalMs,
                $"Starting in gateway (operationId={taskId}), not yet dispatched to Revit.");

            _tasks[taskId] = new TaskRecord { Info = info };
            var deadline = _defaultTimeToLive is { } ttl && ttl > TimeSpan.Zero ? now + ttl : now + RecommendedTimeToLive;
            RevitTaskContext.Current = new RevitTaskContext(taskId, this, deadline);
            McpFeatureLogging.LogTaskCreate(taskId);

            return Task.FromResult(info);
        }

        internal bool TryMarkDispatched(string taskId, string command, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || !_tasks.TryGetValue(taskId, out var record))
                return false;
            var isReadOnly = ToolReadPolicy.IsReadOnly(StripRevitPrefix(command));
            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status) || record.CancelRequestedAfterDispatch || ct.IsCancellationRequested)
                    return false;
                record.DispatchedToHost = true;
                record.IsReadOnlyCommand = isReadOnly;
                record.Info = record.Info with
                {
                    LastUpdatedAt = _utcNow(),
                    StatusMessage = isReadOnly
                        ? $"Running in Revit (operationId={taskId}). Read-only commands have no durable ledger record unless an incident occurs."
                        : $"Running in Revit (operationId={taskId}). If disconnected, check revit_get_operation_status(operationId=\"{taskId}\")."
                };
            }
            McpFeatureLogging.LogTaskDispatch(taskId, command, isReadOnly);
            return true;
        }

        public Task<McpTaskInfo> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(taskId))
                return Task.FromResult<McpTaskInfo>(null);

            var now = _utcNow();
            if (_tasks.TryGetValue(taskId, out var record))
            {
                McpTaskInfo snapshot = null;
                lock (record.Sync)
                {
                    if (IsExpired(record.Info, now))
                    {
                        _tasks.TryRemove(taskId, out _);
                        McpFeatureLogging.LogTaskGet(taskId, "expired", "memory");
                        return Task.FromResult<McpTaskInfo>(null);
                    }

                    if (record.Info.Status != McpTaskStatus.Working)
                        snapshot = record.Info;
                }

                if (snapshot != null)
                {
                    McpFeatureLogging.LogTaskGet(taskId, FormatStatus(snapshot.Status), "memory");
                    return Task.FromResult(snapshot);
                }

                // Read session tracker and SQLite ledger outside record.Sync so disk contention never blocks task state transitions.
                var (session, ledgerOp) = ReadLiveOperationState(taskId);

                lock (record.Sync)
                {
                    if (IsExpired(record.Info, now))
                    {
                        _tasks.TryRemove(taskId, out _);
                        McpFeatureLogging.LogTaskGet(taskId, "expired", "memory");
                        return Task.FromResult<McpTaskInfo>(null);
                    }

                    if (record.Info.Status == McpTaskStatus.Working)
                    {
                        var liveMessage = BuildLiveStatusMessage(record, session, ledgerOp);
                        if (!string.Equals(liveMessage, record.Info.StatusMessage, StringComparison.Ordinal))
                            record.Info = record.Info with { StatusMessage = liveMessage, LastUpdatedAt = now };
                    }

                    snapshot = record.Info;
                }

                McpFeatureLogging.LogTaskGet(taskId, FormatStatus(snapshot.Status), "memory");
                return Task.FromResult(snapshot);
            }

            // TASK-RECOVER: when this server instance has no in-memory entry (e.g., after stdio reconnect),
            // look up the durable FailureLedgerStore receipt by operationId == taskId.
            if (FailureCapture.IsKey(taskId))
            {
                var recovered = RecoverFromLedger(taskId, now);
                if (recovered != null)
                {
                    McpFeatureLogging.LogTaskGet(taskId, FormatStatus(recovered.Status), "ledger");
                    return Task.FromResult(recovered);
                }
            }

            McpFeatureLogging.LogTaskGet(taskId, "missing", "missing");
            return Task.FromResult<McpTaskInfo>(null);
        }

        public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
        {
            if (!_tasks.TryGetValue(taskId, out var record))
                throw new InvalidOperationException($"Task '{taskId}' not found.");

            var isError = IsToolErrorResult(result);
            bool afterCancel;
            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status))
                    return Task.CompletedTask;

                afterCancel = record.CancelRequestedAfterDispatch;
                string message;
                if (afterCancel)
                {
                    message = isError
                        ? $"Completed with error after cancellation was requested (operationId={taskId}). Revit could not stop the in-progress command; inspect this result and revit_get_operation_status(operationId=\"{taskId}\") before retrying."
                        : $"Completed after cancellation was requested (operationId={taskId}). Revit could not stop the in-progress command; inspect this result and revit_get_operation_status(operationId=\"{taskId}\") before retrying.";
                }
                else
                {
                    message = isError
                        ? $"Completed with error (operationId={taskId}); inspect this result and revit_get_operation_status(operationId=\"{taskId}\") before retrying."
                        : $"Completed (operationId={taskId}).";
                }

                record.Info = record.Info with
                {
                    Status = McpTaskStatus.Completed,
                    Result = result,
                    StatusMessage = message,
                    LastUpdatedAt = _utcNow(),
                };
                McpFeatureLogging.LogTaskComplete(taskId, isError, afterCancel);
            }

            return Task.CompletedTask;
        }

        public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
        {
            if (!_tasks.TryGetValue(taskId, out var record))
                throw new InvalidOperationException($"Task '{taskId}' not found.");

            bool afterCancel;
            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status))
                    return Task.CompletedTask;

                afterCancel = record.CancelRequestedAfterDispatch;
                var message = afterCancel
                    ? $"Failed after cancellation was requested (operationId={taskId}); inspect revit_get_operation_status(operationId=\"{taskId}\") before retrying."
                    : record.Info.StatusMessage;

                record.Info = record.Info with
                {
                    Status = McpTaskStatus.Failed,
                    Error = error,
                    StatusMessage = message,
                    LastUpdatedAt = _utcNow(),
                };
                McpFeatureLogging.LogTaskFail(taskId, afterCancel);
            }

            return Task.CompletedTask;
        }

        public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                return Task.FromResult(false);

            if (!_tasks.TryGetValue(taskId, out var record))
            {
                // TASK-CANCEL-HONEST (FC-BUSY-04): if the client asks to cancel a running operation recovered
                // from the durable ledger after reconnect, remember the request so tasks/get surfaces the warning.
                var stage = "missing";
                if (FailureCapture.IsKey(taskId))
                {
                    var ledgerOp = TryReadLedgerOperation(taskId);
                    var execution = ledgerOp?.Execution;
                    if (IsActiveExecution(execution))
                    {
                        _cancelledRecoveredTasks[taskId] = _utcNow();
                        stage = "recovered";
                    }
                }
                McpFeatureLogging.LogTaskCancel(taskId, stage);
                return Task.FromResult(false);
            }

            string cancelStage;
            bool cancelled;
            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status))
                {
                    cancelStage = "terminal";
                    cancelled = false;
                }
                else if (record.DispatchedToHost)
                {
                    // TASK-CANCEL-HONEST (FC-BUSY-04): once sent to Revit, the add-in cannot abort an active
                    // ExternalEvent. Keep the task Working with an explicit warning rather than claiming Cancelled.
                    record.CancelRequestedAfterDispatch = true;
                    record.Info = record.Info with
                    {
                        StatusMessage = HonestPostDispatchCancelMessage(taskId, execution: null),
                        LastUpdatedAt = _utcNow(),
                    };
                    cancelStage = "post_dispatch";
                    cancelled = false;
                }
                else
                {
                    record.Info = record.Info with
                    {
                        Status = McpTaskStatus.Cancelled,
                        StatusMessage = $"Cancelled before dispatch to Revit (operationId={taskId}); nothing started.",
                        LastUpdatedAt = _utcNow(),
                    };
                    cancelStage = "pre_dispatch";
                    cancelled = true;
                }
            }

            McpFeatureLogging.LogTaskCancel(taskId, cancelStage);
            return Task.FromResult(cancelled);
        }

        private static string FormatStatus(McpTaskStatus status) => status switch
        {
            McpTaskStatus.Working => "working",
            McpTaskStatus.Completed => "completed",
            McpTaskStatus.Failed => "failed",
            McpTaskStatus.Cancelled => "cancelled",
            McpTaskStatus.InputRequired => "input_required",
            _ => "unknown"
        };

        public Task ResolveInputRequestsAsync(
            string taskId,
            IDictionary<string, InputResponse> inputResponses,
            CancellationToken cancellationToken = default)
        {
            if (!_tasks.TryGetValue(taskId, out var record))
                throw new InvalidOperationException($"Task '{taskId}' not found.");

            var resolved = new List<InputResponseReceivedEventArgs>();
            lock (record.Sync)
            {
                if (!IsTerminal(record.Info.Status) && record.Info.InputRequests is { Count: > 0 } currentRequests && inputResponses != null)
                {
                    var remaining = currentRequests.ToImmutableDictionary();
                    foreach (var kvp in inputResponses)
                    {
                        if (remaining.ContainsKey(kvp.Key))
                        {
                            remaining = remaining.Remove(kvp.Key);
                            resolved.Add(new InputResponseReceivedEventArgs
                            {
                                TaskId = taskId,
                                RequestId = kvp.Key,
                                Response = kvp.Value,
                            });
                        }
                    }

                    record.Info = record.Info with
                    {
                        InputRequests = remaining,
                        Status = remaining.Count == 0 ? McpTaskStatus.Working : McpTaskStatus.InputRequired,
                        LastUpdatedAt = _utcNow(),
                    };
                }
            }

            foreach (var item in resolved)
                InputResponseReceived?.Invoke(item);

            return Task.CompletedTask;
        }

        public Task SetInputRequestsAsync(
            string taskId,
            IDictionary<string, InputRequest> inputRequests,
            CancellationToken cancellationToken = default)
        {
            if (!_tasks.TryGetValue(taskId, out var record))
                throw new InvalidOperationException($"Task '{taskId}' not found.");

            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status))
                    return Task.CompletedTask;

                var merged = record.Info.InputRequests?.ToImmutableDictionary() ?? ImmutableDictionary<string, InputRequest>.Empty;
                if (inputRequests != null)
                {
                    foreach (var kvp in inputRequests)
                        merged = merged.SetItem(kvp.Key, kvp.Value);
                }

                record.Info = record.Info with
                {
                    InputRequests = merged,
                    Status = McpTaskStatus.InputRequired,
                    LastUpdatedAt = _utcNow(),
                };
            }

            return Task.CompletedTask;
        }

        private (JObject Session, FailureOperation Ledger) ReadLiveOperationState(string taskId)
        {
            JObject session = null;
            try { session = _sessionStatusLookup(taskId); }
            catch (Exception ex) { DiagnosticLog.Write("Tasks", $"Session operation lookup failed for {taskId}: {ex.GetType().Name}: {ex.Message}"); }

            var ledgerOp = TryReadLedgerOperation(taskId);
            return (session, ledgerOp);
        }

        private FailureOperation TryReadLedgerOperation(string taskId)
        {
            try { return _ledgerFactory()?.GetOperation(taskId); }
            catch (Exception ex)
            {
                DiagnosticLog.Write("Tasks", $"Ledger operation lookup failed for {taskId}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        internal static string ResolveLiveExecution(string ledgerExecution, string sessionExecution)
        {
            if (ledgerExecution is "completed" or "interrupted_unknown")
                return ledgerExecution;
            if (sessionExecution == "waiting_for_user" || ledgerExecution == "waiting_for_user")
                return "waiting_for_user";
            if (sessionExecution == "timed_out_running" || ledgerExecution == "timed_out_running")
                return "timed_out_running";
            return ledgerExecution ?? sessionExecution;
        }

        private static string BuildLiveStatusMessage(TaskRecord record, JObject session, FailureOperation ledgerOp)
        {
            var taskId = record.Info.TaskId;
            var execution = ResolveLiveExecution(ledgerOp?.Execution, session?.Value<string>("execution"));
            if (record.CancelRequestedAfterDispatch)
                return HonestPostDispatchCancelMessage(taskId, execution);

            if (string.IsNullOrEmpty(execution))
                return record.Info.StatusMessage;

            if (execution == "waiting_for_user")
            {
                var dialog = ledgerOp?.WindowDialog ?? session?["windowDialog"] as JObject;
                var title = dialog?.Value<string>("title");
                var suffix = string.IsNullOrEmpty(title) ? string.Empty : $" ({title})";
                return $"Waiting for user in Revit dialog{suffix} (operationId={taskId}). Use revit_get_operation_status(operationId=\"{taskId}\") for dialog details; do not retry.";
            }

            if (execution == "timed_out_running")
                return $"Revit is still running after gateway wait timeout (operationId={taskId}). Check revit_get_operation_status(operationId=\"{taskId}\"); do not retry.";

            if (execution == "running" || execution == "queued")
            {
                return record.IsReadOnlyCommand
                    ? $"Running in Revit (operationId={taskId}, execution={execution}). Read-only commands have no durable ledger record unless an incident occurs."
                    : $"Running in Revit (operationId={taskId}, execution={execution}). Check revit_get_operation_status(operationId=\"{taskId}\") if interrupted.";
            }

            return record.Info.StatusMessage;
        }

        private McpTaskInfo RecoverFromLedger(string taskId, DateTimeOffset now)
        {
            var op = TryReadLedgerOperation(taskId);
            if (op == null) return null;

            var execution = op.Execution ?? "running";
            var cancelRequested = _cancelledRecoveredTasks.ContainsKey(taskId);

            if (IsActiveExecution(execution))
            {
                var msg = cancelRequested
                    ? HonestPostDispatchCancelMessage(taskId, execution)
                    : execution == "waiting_for_user"
                        ? $"Waiting for user in Revit (operationId={taskId}, recovered from ledger). Check revit_get_operation_status(operationId=\"{taskId}\"); do not retry."
                        : $"Operation still {execution} in Revit (operationId={taskId}, recovered from ledger). Check revit_get_operation_status(operationId=\"{taskId}\"); do not retry.";
                return new McpTaskInfo(
                    taskId,
                    McpTaskStatus.Working,
                    op.StartedUtc,
                    op.UpdatedUtc,
                    _defaultTimeToLive,
                    _defaultPollIntervalMs,
                    msg);
            }

            if (execution == "completed" || execution == "interrupted_unknown")
            {
                // TASK-TIMING: keep completed task results for at least TTL measured from completion/update time.
                if (_defaultTimeToLive is { } ttl && ttl > TimeSpan.Zero && now - op.UpdatedUtc >= ttl)
                    return null;

                var isInterrupted = execution == "interrupted_unknown";
                var payload = new JObject
                {
                    ["available"] = true,
                    ["recoveredFromLedger"] = true,
                    ["operationId"] = op.OperationId,
                    ["tool"] = op.Tool,
                    ["execution"] = execution,
                    ["outcome"] = op.Outcome,
                    ["mutationApplied"] = op.MutationApplied,
                    ["complete"] = op.Complete,
                    ["note"] = isInterrupted
                        ? $"Revit connection was interrupted mid-operation; inspect revit_get_operation_status(operationId=\"{taskId}\") and any output files before retrying."
                        : $"Recovered from local operation ledger after gateway restart; call revit_get_operation_status(operationId=\"{taskId}\") for full details."
                };
                if (op.FinalResult != null)
                    payload["finalResult"] = op.FinalResult.DeepClone();

                // Read-only commands persisted due to an incident record Outcome="not_started" on success (FailureCompletion.Apply).
                var isReadOnly = ToolReadPolicy.IsReadOnly(StripRevitPrefix(op.Tool));
                var readOnlySucceeded = isReadOnly
                    && string.Equals(op.Outcome, "not_started", StringComparison.Ordinal)
                    && op.FinalResult?["success"]?.Value<bool?>() == true
                    && !op.RequiresReview;
                var succeeded = !isInterrupted
                    && op.FinalResult?["success"]?.Value<bool?>() != false
                    && (string.Equals(op.Outcome, "committed", StringComparison.Ordinal) || readOnlySucceeded);

                var callResult = new CallToolResult
                {
                    IsError = !succeeded,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = payload.ToString(Newtonsoft.Json.Formatting.Indented) }
                    }
                };
                var resultJson = JsonSerializer.SerializeToElement(callResult, McpJsonUtilities.ProtocolOptions.GetTypeInfo<CallToolResult>());
                string statusMessage;
                if (isInterrupted)
                    statusMessage = $"Recovered interrupted operation from ledger (operationId={taskId}, execution=interrupted_unknown); inspect revit_get_operation_status before retrying.";
                else if (cancelRequested)
                    statusMessage = $"Recovered completed operation from ledger after cancellation was requested (operationId={taskId}, outcome={op.Outcome ?? "unknown"}); inspect revit_get_operation_status before retrying.";
                else
                    statusMessage = $"Recovered completed operation from ledger (operationId={taskId}, outcome={op.Outcome ?? "unknown"}).";

                return new McpTaskInfo(
                    taskId,
                    McpTaskStatus.Completed,
                    op.StartedUtc,
                    op.UpdatedUtc,
                    _defaultTimeToLive,
                    _defaultPollIntervalMs,
                    statusMessage,
                    Result: resultJson);
            }

            return null;
        }

        private static string HonestPostDispatchCancelMessage(string taskId, string execution)
        {
            var state = string.IsNullOrEmpty(execution) ? string.Empty : $" (execution={execution})";
            return $"Cancellation requested, but Revit cannot stop an in-progress command (operationId={taskId}){state}. "
                 + $"The operation may still be running or may already have changed files or model state. "
                 + $"Keep polling tasks/get or check revit_get_operation_status(operationId=\"{taskId}\") before retrying.";
        }

        private static bool IsActiveExecution(string execution)
            => execution is "waiting_for_user" or "timed_out_running" or "running" or "queued";

        private static bool IsTerminal(McpTaskStatus status)
            => status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;

        // TASK-TIMING: never evict an active Working task; expire terminal entries TTL after completion (LastUpdatedAt).
        private static bool IsExpired(McpTaskInfo info, DateTimeOffset now)
            => IsTerminal(info.Status)
               && info.TimeToLive is { } ttl
               && ttl > TimeSpan.Zero
               && now - info.LastUpdatedAt >= ttl;

        private static bool IsToolErrorResult(JsonElement result)
            => result.ValueKind == JsonValueKind.Object
               && result.TryGetProperty("isError", out var prop)
               && prop.ValueKind == JsonValueKind.True;

        private static string StripRevitPrefix(string toolOrCommand)
            => toolOrCommand != null && toolOrCommand.StartsWith("revit_", StringComparison.Ordinal)
                ? toolOrCommand.Substring(6)
                : toolOrCommand;

        internal void SweepExpired(DateTimeOffset now)
        {
            long last = Interlocked.Read(ref _lastSweepTicks);
            if (now.UtcTicks - last < SweepIntervalTicks)
                return;

            if (Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
                return;

            foreach (var kvp in _tasks)
            {
                bool expired;
                lock (kvp.Value.Sync)
                    expired = IsExpired(kvp.Value.Info, now);
                if (expired)
                    _tasks.TryRemove(kvp.Key, out _);
            }

            if (_defaultTimeToLive is { } ttl && ttl > TimeSpan.Zero)
            {
                foreach (var kvp in _cancelledRecoveredTasks)
                {
                    if (now - kvp.Value >= ttl)
                        _cancelledRecoveredTasks.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
