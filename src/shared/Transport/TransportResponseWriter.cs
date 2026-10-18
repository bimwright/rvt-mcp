using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>One background writer owns the wire; UI and timer callbacks only enqueue immutable frames.</summary>
    internal sealed class TransportResponseWriter : IDisposable
    {
        private readonly BlockingCollection<string> _frames = new BlockingCollection<string>(256);
        private const long MaxPendingBytes = 32L * 1024 * 1024;
        private long _pendingBytes;
        private readonly Action _onWriteFailure;
        private readonly object _queueGate = new object();
        private readonly StreamWriter _writer;
        private readonly Thread _thread;
        private bool _closing;
        private int _pendingFrames;
        internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);
        private int _inFlight;
        private long _lastActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        /// <summary>Requests dispatched on this connection whose execution has not completed yet.</summary>
        internal int InFlight => Volatile.Read(ref _inFlight);
        internal void Touch() => Interlocked.Exchange(ref _lastActivityTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        /// <summary>True when nothing is in flight and the connection has been quiet for <paramref name="limit"/>.</summary>
        internal bool IsIdleFor(TimeSpan limit) =>
            !PendingRequestLifecycle.IsHostWaitingForUser && InFlight == 0 && Volatile.Read(ref _pendingFrames) == 0
                && (System.Diagnostics.Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastActivityTicks)) / (double)System.Diagnostics.Stopwatch.Frequency >= limit.TotalSeconds;
        internal TransportResponseWriter(StreamWriter writer, Action onWriteFailure = null, Action onDrained = null)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _onWriteFailure = onWriteFailure;
            _thread = new Thread(() =>
            {
                try
                {
                    foreach (var frame in _frames.GetConsumingEnumerable())
                    {
                        _writer.WriteLine(frame);
                        _writer.Flush();
                        Touch();
                        Interlocked.Decrement(ref _pendingFrames);
                        Interlocked.Add(ref _pendingBytes, -Encoding.UTF8.GetByteCount(frame));
                    }
                    onDrained?.Invoke();
                }
                catch
                {
                    CompleteAdding();
                    // Wake a transport reader parked on the failed connection so a new client can connect.
                    try { onWriteFailure?.Invoke(); } catch { }
                }
                finally
                {
                    // No caller may flush/dispose StreamWriter while this thread is writing it.
                    try { _writer.Dispose(); } catch { }
                    lock (_queueGate) { _frames.Dispose(); }
                }
            })
            { IsBackground = true, Name = "RvtMcp.TransportWriter" };
            _thread.Start();
        }
        internal void WriteLine(string frame)
        {
            var overflow = false;
            lock (_queueGate)
            {
                if (_closing) return;
                var bytes = Encoding.UTF8.GetByteCount(frame);
                if (_pendingFrames >= 256 || _pendingBytes + bytes > MaxPendingBytes)
                {
                    _closing = true; _frames.CompleteAdding(); overflow = true;
                }
                else
                {
                    Interlocked.Increment(ref _pendingFrames);
                    Interlocked.Add(ref _pendingBytes, bytes);
                    _frames.Add(frame);
                }
            }
            if (overflow) { DiagnosticLog.Write("ResponseWriter", "Peer did not drain responses; connection closed. Retrieve operation outcome before retrying writes."); try { _onWriteFailure?.Invoke(); } catch { } }
        }
        internal void Dispatch(JObject request, string line, Action<string, TaskCompletionSource<string>> callback)
        {
            var reject = false;
            lock (_queueGate)
            {
                if (_closing || _pendingFrames + InFlight >= 256)
                {
                    if (!_closing) { _closing = true; _frames.CompleteAdding(); }
                    reject = true;
                }
            }
            if (reject)
            {
                try { _onWriteFailure?.Invoke(); } catch { }
                return; // No execution was dispatched; prior operations keep their persisted receipts.
            }
            var id = request.Value<string>("id");
            var command = request.Value<string>("command");
            // Already authenticated by the transport. Health never enters the Revit UI queue.
            if (string.Equals(command, "health", StringComparison.Ordinal))
            {
                WriteLine(new JObject
                {
                    ["id"] = id, ["success"] = true,
                    ["data"] = HostHealthSnapshot.Capture(InFlight, PendingRequestLifecycle.IsHostWaitingForUser)
                }.ToString(Formatting.None));
                return;
            }
            if (!ToolReadPolicy.IsReadOnly(command) && request.Value<int?>("failure_protocol") != 1)
            {
                WriteLine(new JObject { ["id"] = id, ["success"] = false, ["error"] = ToolReadPolicy.GatewayPairingRequired }.ToString(Formatting.None));
                return;
            }
            if (PendingRequestLifecycle.IsHostWaitingForUser)
            {
                var busy = new JObject { ["id"] = id, ["success"] = false, ["error"] = "REVIT_BUSY: Revit is waiting for native user input. Local failure and operation queries remain available." };
                var correlated = PendingRequestLifecycle.HostBusyMetadata;
                if (correlated != null)
                    busy["_operation"] = new JObject
                    {
                        ["operationId"] = request["operation_id"]?.DeepClone(),
                        ["execution"] = "completed",
                        ["outcome"] = "not_started",
                        ["mutationApplied"] = false,
                        ["complete"] = true,
                        ["hostBusy"] = true,
                        ["correlation"] = correlated["correlation"],
                        ["correlatedOperationId"] = correlated["operationId"],
                        ["modelScopeKey"] = correlated["modelScopeKey"]
                    };
                WriteLine(busy.ToString(Formatting.None));
                return;
            }
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _inFlight);
            Touch();
            var lifecycle = PendingRequestLifecycle.For(tcs);
            lifecycle.Configure(id, request.Value<string>("operation_id"), WriteLine);
            var seconds = request.Value<int?>("timeout_seconds");
            var timeout = RequestWait.EffectiveTimeout(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null);
            Timer timer = null;
            timer = new Timer(_ =>
            {
                var response = lifecycle.Timeout();
                // No-start cancellation completes execution itself. Started requests retain final delivery.
                if (!tcs.Task.IsCompleted) WriteLine(response);
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            tcs.Task.ContinueWith(task =>
            {
                timer.Dispose();
                if (task.Status == TaskStatus.RanToCompletion) WriteLine(task.Result);
                Touch();
                Interlocked.Decrement(ref _inFlight);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            timer.Change(timeout, Timeout.InfiniteTimeSpan);
            try { callback(line, tcs); }
            catch
            {
                tcs.TrySetResult(new JObject { ["id"] = id, ["success"] = false, ["error"] = "Request could not be queued." }.ToString(Formatting.None));
            }
        }
        private void CompleteAdding()
        {
            lock (_queueGate)
            {
                if (_closing) return;
                _closing = true;
                _frames.CompleteAdding();
            }
        }

        public void Dispose()
        {
            CompleteAdding();
            // Drain queued terminal frames, not unfinished Revit operations. If a peer is not reading,
            // the transport closes its stream after this deadline to abort the blocked writer.
            if (Thread.CurrentThread != _thread && !_thread.Join(DrainTimeout))
            {
                try { _onWriteFailure?.Invoke(); } catch { }
                if (_onWriteFailure != null && !_thread.Join(DrainTimeout)) throw new TimeoutException("Response writer has not stopped.");
            }
        }
    }
}
