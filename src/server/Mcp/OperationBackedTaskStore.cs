using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;

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
            public string Command;
        }

        private readonly ConcurrentDictionary<string, TaskRecord> _tasks = new ConcurrentDictionary<string, TaskRecord>(StringComparer.Ordinal);
        private readonly TimeSpan? _defaultTimeToLive;
        private readonly long _defaultPollIntervalMs;
        private readonly Func<FailureLedgerStore> _ledgerFactory;
        private readonly Func<string, JObject> _sessionStatusLookup;
        private long _lastSweepTicks = DateTimeOffset.UtcNow.UtcTicks;

        public event Action<InputResponseReceivedEventArgs> InputResponseReceived;

        internal OperationBackedTaskStore(
            TimeSpan? timeToLive = null,
            long pollIntervalMs = RecommendedPollIntervalMs,
            Func<FailureLedgerStore> ledgerFactory = null,
            Func<string, JObject> sessionStatusLookup = null)
        {
            if (pollIntervalMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(pollIntervalMs), "Poll interval must be positive.");
            _defaultTimeToLive = timeToLive ?? RecommendedTimeToLive;
            _defaultPollIntervalMs = pollIntervalMs;
            _ledgerFactory = ledgerFactory ?? (() => FailureTools.StoreFactory());
            _sessionStatusLookup = sessionStatusLookup ?? (id => ToolGateway.PreviousOperations.Status(id));
        }

        // Must stay synchronous (no async/await) so setting RevitTaskContext.Current mutates the caller's
        // ExecutionContext in RunAsTaskAsync before Task.Run captures it for ExecuteTaskAsync.
        public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
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

            return Task.FromResult(info);
        }

        internal bool TryMarkDispatched(string taskId, string command, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || !_tasks.TryGetValue(taskId, out var record))
                return false;
            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status) || record.CancelRequestedAfterDispatch || ct.IsCancellationRequested)
                    return false;
                record.DispatchedToHost = true;
                record.Command = command;
                record.Info = record.Info with
                {
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                    StatusMessage = $"Running in Revit (operationId={taskId}). If disconnected, check revit_get_operation_status(operationId=\"{taskId}\")."
                };
                return true;
            }
        }

        public Task<McpTaskInfo> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(taskId))
                return Task.FromResult<McpTaskInfo>(null);

            var now = DateTimeOffset.UtcNow;
            if (_tasks.TryGetValue(taskId, out var record))
            {
                lock (record.Sync)
                {
                    if (IsExpired(record.Info, now))
                    {
                        _tasks.TryRemove(taskId, out _);
                        return Task.FromResult<McpTaskInfo>(null);
                    }

                    if (record.Info.Status != McpTaskStatus.Working)
                        return Task.FromResult(record.Info);
                }

                // Read session tracker and SQLite ledger outside record.Sync so disk contention never blocks task state transitions.
                var (session, ledgerOp) = ReadLiveOperationState(taskId);

                lock (record.Sync)
                {
                    if (IsExpired(record.Info, now))
                    {
                        _tasks.TryRemove(taskId, out _);
                        return Task.FromResult<McpTaskInfo>(null);
                    }

                    if (record.Info.Status == McpTaskStatus.Working)
                    {
                        var liveMessage = BuildLiveStatusMessage(record, session, ledgerOp);
                        if (!string.Equals(liveMessage, record.Info.StatusMessage, StringComparison.Ordinal))
                            record.Info = record.Info with { StatusMessage = liveMessage, LastUpdatedAt = now };
                    }

                    return Task.FromResult(record.Info);
                }
            }

            // TASK-RECOVER: when this server instance has no in-memory entry (e.g., after stdio reconnect),
            // look up the durable FailureLedgerStore receipt by operationId == taskId.
            if (FailureCapture.IsKey(taskId))
            {
                var recovered = RecoverFromLedger(taskId, now);
                if (recovered != null)
                    return Task.FromResult(recovered);
            }

            return Task.FromResult<McpTaskInfo>(null);
        }

        public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
        {
            if (!_tasks.TryGetValue(taskId, out var record))
                throw new InvalidOperationException($"Task '{taskId}' not found.");

            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status))
                    return Task.CompletedTask;

                var message = record.CancelRequestedAfterDispatch
                    ? $"Completed after cancellation was requested (operationId={taskId}). Revit could not stop the in-progress command; inspect this result and revit_get_operation_status(operationId=\"{taskId}\") before retrying."
                    : $"Completed (operationId={taskId}).";

                record.Info = record.Info with
                {
                    Status = McpTaskStatus.Completed,
                    Result = result,
                    StatusMessage = message,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            return Task.CompletedTask;
        }

        public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
        {
            if (!_tasks.TryGetValue(taskId, out var record))
                throw new InvalidOperationException($"Task '{taskId}' not found.");

            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status))
                    return Task.CompletedTask;

                var message = record.CancelRequestedAfterDispatch
                    ? $"Failed after cancellation was requested (operationId={taskId}); inspect revit_get_operation_status(operationId=\"{taskId}\") before retrying."
                    : record.Info.StatusMessage;

                record.Info = record.Info with
                {
                    Status = McpTaskStatus.Failed,
                    Error = error,
                    StatusMessage = message,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            return Task.CompletedTask;
        }

        public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
        {
            if (!_tasks.TryGetValue(taskId, out var record))
                return Task.FromResult(false);

            lock (record.Sync)
            {
                if (IsTerminal(record.Info.Status))
                    return Task.FromResult(false);

                // TASK-CANCEL-HONEST (FC-BUSY-04): once sent to Revit, the add-in cannot abort an active
                // ExternalEvent. Keep the task Working with an explicit warning rather than claiming Cancelled.
                if (record.DispatchedToHost)
                {
                    record.CancelRequestedAfterDispatch = true;
                    record.Info = record.Info with
                    {
                        StatusMessage = HonestPostDispatchCancelMessage(taskId, execution: null),
                        LastUpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return Task.FromResult(false);
                }

                record.Info = record.Info with
                {
                    Status = McpTaskStatus.Cancelled,
                    StatusMessage = $"Cancelled before dispatch to Revit (operationId={taskId}); nothing started.",
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                };
                return Task.FromResult(true);
            }
        }

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
                        LastUpdatedAt = DateTimeOffset.UtcNow,
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
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            return Task.CompletedTask;
        }

        private (JObject Session, FailureOperation Ledger) ReadLiveOperationState(string taskId)
        {
            JObject session = null;
            try { session = _sessionStatusLookup(taskId); } catch { }

            FailureOperation ledgerOp = null;
            try { ledgerOp = _ledgerFactory()?.GetOperation(taskId); } catch { }

            return (session, ledgerOp);
        }

        private static string BuildLiveStatusMessage(TaskRecord record, JObject session, FailureOperation ledgerOp)
        {
            var taskId = record.Info.TaskId;
            var execution = ledgerOp?.Execution ?? session?.Value<string>("execution");
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
                return $"Running in Revit (operationId={taskId}, execution={execution}). Check revit_get_operation_status(operationId=\"{taskId}\") if interrupted.";

            return record.Info.StatusMessage;
        }

        private McpTaskInfo RecoverFromLedger(string taskId, DateTimeOffset now)
        {
            FailureOperation op;
            try { op = _ledgerFactory()?.GetOperation(taskId); }
            catch { return null; }

            if (op == null) return null;
            if (_defaultTimeToLive is { } ttl && ttl > TimeSpan.Zero && now - op.StartedUtc >= ttl)
                return null;

            var execution = op.Execution ?? "running";
            if (execution == "waiting_for_user" || execution == "timed_out_running" || execution == "running" || execution == "queued")
            {
                var msg = execution == "waiting_for_user"
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

                var failedOutcome = op.Outcome is "not_started" or "rolled_back" or "partial" or "cancelled" or "unknown";
                var succeeded = !isInterrupted
                    && !failedOutcome
                    && op.FinalResult?["success"]?.Value<bool?>() != false
                    && string.Equals(op.Outcome, "committed", StringComparison.Ordinal);
                var callResult = new CallToolResult
                {
                    IsError = !succeeded,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = payload.ToString(Newtonsoft.Json.Formatting.Indented) }
                    }
                };
                var resultJson = JsonSerializer.SerializeToElement(callResult, McpJsonUtilities.ProtocolOptions.GetTypeInfo<CallToolResult>());
                var statusMessage = isInterrupted
                    ? $"Recovered interrupted operation from ledger (operationId={taskId}, execution=interrupted_unknown); inspect revit_get_operation_status before retrying."
                    : $"Recovered completed operation from ledger (operationId={taskId}, outcome={op.Outcome ?? "unknown"}).";
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

        private static bool IsTerminal(McpTaskStatus status)
            => status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;

        private static bool IsExpired(McpTaskInfo info, DateTimeOffset now)
            => info.TimeToLive is { } ttl && ttl > TimeSpan.Zero && now - info.CreatedAt >= ttl;

        private void SweepExpired(DateTimeOffset now)
        {
            long last = Interlocked.Read(ref _lastSweepTicks);
            if (now.UtcTicks - last < SweepIntervalTicks)
                return;

            if (Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
                return;

            foreach (var kvp in _tasks)
            {
                if (IsExpired(kvp.Value.Info, now))
                    _tasks.TryRemove(kvp.Key, out _);
            }
        }
    }
}
