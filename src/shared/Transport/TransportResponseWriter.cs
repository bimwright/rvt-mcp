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
        private readonly StreamWriter _writer;
        private readonly Thread _thread;
        internal TransportResponseWriter(StreamWriter writer)
        {
            _writer = writer;
            _thread = new Thread(() =>
            {
                try { foreach (var frame in _frames.GetConsumingEnumerable()) _writer.WriteLine(frame); }
                catch { _frames.CompleteAdding(); }
            }) { IsBackground = true, Name = "RvtMcp.TransportWriter" };
            _thread.Start();
        }
        internal void WriteLine(string frame)
        {
            try { if (!_frames.IsAddingCompleted) _frames.Add(frame); } catch (InvalidOperationException) { }
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
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            timer.Change(timeout, Timeout.InfiniteTimeSpan);
            try { callback(line, tcs); }
            catch
            {
                tcs.TrySetResult(new JObject { ["id"] = id, ["success"] = false, ["error"] = "Request could not be queued." }.ToString(Formatting.None));
            }
        }
        public void Dispose() { _frames.CompleteAdding(); }
    }
}
