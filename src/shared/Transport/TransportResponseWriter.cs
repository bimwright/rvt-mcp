using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>One background writer owns the wire; UI and timer callbacks only enqueue immutable frames.</summary>
    internal sealed class TransportResponseWriter : IDisposable
    {
        private readonly BlockingCollection<string> _frames = new BlockingCollection<string>();
        private readonly object _queueGate = new object();
        private readonly StreamWriter _writer;
        private readonly Thread _thread;
        private bool _closing;
        private int _pendingFrames;
        internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);
        private int _inFlight;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;
        /// <summary>Requests dispatched on this connection whose execution has not completed yet.</summary>
        internal int InFlight => Volatile.Read(ref _inFlight);
        internal void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        /// <summary>True when nothing is in flight and the connection has been quiet for <paramref name="limit"/>.</summary>
        internal bool IsIdleFor(TimeSpan limit) =>
            InFlight == 0 && Volatile.Read(ref _pendingFrames) == 0
                && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks) >= limit.Ticks;
        internal TransportResponseWriter(StreamWriter writer, Action onWriteFailure = null, Action onDrained = null)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
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
            }) { IsBackground = true, Name = "RvtMcp.TransportWriter" };
            _thread.Start();
        }
        internal void WriteLine(string frame)
        {
            lock (_queueGate)
            {
                if (_closing) return;
                Interlocked.Increment(ref _pendingFrames);
                _frames.Add(frame);
            }
        }
        internal void Dispatch(JObject request, string line, Action<string, TaskCompletionSource<string>> callback)
        {
            var id = request.Value<string>("id");
            var command = request.Value<string>("command");
            if (!ToolReadPolicy.IsReadOnly(command) && request.Value<int?>("failure_protocol") != 1)
            {
                WriteLine(new JObject { ["id"] = id, ["success"] = false, ["error"] = "PAIRING_REQUIRED: update the gateway and Revit add-in together before model mutations." }.ToString(Formatting.None));
                return;
            }
            if (PendingRequestLifecycle.IsHostWaitingForUser)
            {
                var busy = new JObject { ["id"] = id, ["success"] = false, ["error"] = "REVIT_BUSY: Revit is waiting for native user input. Local failure and operation queries remain available." };
                var correlated = PendingRequestLifecycle.HostBusyMetadata;
                if (correlated != null)
                    busy["_operation"] = new JObject
                    {
                        ["operationId"] = request["operation_id"]?.DeepClone(), ["execution"] = "completed", ["outcome"] = "not_started",
                        ["mutationApplied"] = false, ["complete"] = true, ["hostBusy"] = true,
                        ["correlation"] = correlated["correlation"], ["correlatedOperationId"] = correlated["operationId"], ["modelScopeKey"] = correlated["modelScopeKey"]
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
            if (Thread.CurrentThread != _thread) _thread.Join(DrainTimeout);
        }
    }
}
