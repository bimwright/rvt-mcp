using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class TransportResponseWriterTests
    {
        private static JObject Request(string id) =>
            new JObject { ["id"] = id, ["command"] = "get_current_view_info", ["failure_protocol"] = 1 };

        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "Condition was not met within 5 s.");
                await Task.Delay(10);
            }
        }

        [Fact]
        public async Task Tcp_background_writer_delivers_while_the_reader_waits_for_the_next_request()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var serverSide = await accept;
                var serverStream = serverSide.GetStream();
                using var serverReader = new StreamReader(serverStream, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new TransportResponseWriter(new StreamWriter(serverStream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true });
                using var clientReader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, leaveOpen: true);

                var pendingRead = Task.Run(() => { try { return serverReader.ReadLine(); } catch (IOException) { return null; } });
                await Task.Delay(100);
                writer.WriteLine("{\"id\":\"catalog\",\"success\":true}");

                var handshake = clientReader.ReadLineAsync();
                Assert.True(await Task.WhenAny(handshake, Task.Delay(TimeSpan.FromSeconds(2))) == handshake,
                    "The handshake was not delivered while the server read was pending.");
                Assert.False(pendingRead.IsCompleted);
                client.Dispose(); // lets the pending server read finish
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public async Task A_connection_is_idle_only_when_no_request_is_in_flight_and_the_clock_restarts_on_completion()
        {
            PendingRequestLifecycle.SetHostBusy(false);
            using var wire = new MemoryStream();
            using var writer = new TransportResponseWriter(new StreamWriter(wire, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true });
            Assert.Equal(0, writer.InFlight);

            TaskCompletionSource<string> execution = null;
            var request = Request("r1");
            writer.Dispatch(request, request.ToString(), (line, tcs) => execution = tcs);
            Assert.Equal(1, writer.InFlight);

            await Task.Delay(60);
            // Quiet for longer than the limit, but the operation is still running or waiting for the user.
            Assert.False(writer.IsIdleFor(TimeSpan.FromMilliseconds(20)));

            execution.TrySetResult("{\"id\":\"r1\",\"success\":true}");
            await WaitUntil(() => writer.InFlight == 0);
            Assert.False(writer.IsIdleFor(TimeSpan.FromSeconds(30)), "The idle clock must restart when the request finishes.");
            await Task.Delay(80);
            Assert.True(writer.IsIdleFor(TimeSpan.FromMilliseconds(40)));
        }

        [Fact]
        public async Task A_request_that_cannot_be_queued_does_not_leave_the_connection_busy()
        {
            PendingRequestLifecycle.SetHostBusy(false);
            using var wire = new MemoryStream();
            using var writer = new TransportResponseWriter(new StreamWriter(wire, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true });
            var request = Request("r2");
            writer.Dispatch(request, request.ToString(), (line, tcs) => throw new InvalidOperationException("queue full"));
            await WaitUntil(() => writer.InFlight == 0);
        }

        [Fact]
        public void Dispose_drains_and_flushes_all_enqueued_frames_before_returning()
        {
            using var wire = new MemoryStream();
            var streamWriter = new OwnershipWriter(wire);
            var writer = new TransportResponseWriter(streamWriter);
            for (var i = 0; i < 100; i++) writer.WriteLine("frame-" + i);

            writer.Dispose();
            writer.WriteLine("late frame");
            writer.Dispose();

            var lines = Encoding.UTF8.GetString(wire.ToArray()).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(100, lines.Length);
            for (var i = 0; i < lines.Length; i++) Assert.Equal("frame-" + i, lines[i]);
            Assert.True(streamWriter.WasDisposed);
            Assert.Single(streamWriter.OwnerThreads);
            Assert.DoesNotContain(Thread.CurrentThread.ManagedThreadId, streamWriter.OwnerThreads);
        }

        private sealed class OwnershipWriter : StreamWriter
        {
            internal readonly HashSet<int> OwnerThreads = new HashSet<int>();
            internal bool WasDisposed;
            internal OwnershipWriter(Stream stream) : base(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { }
            public override void WriteLine(string value)
            {
                OwnerThreads.Add(Thread.CurrentThread.ManagedThreadId);
                base.WriteLine(value);
            }
            public override void Flush()
            {
                OwnerThreads.Add(Thread.CurrentThread.ManagedThreadId);
                base.Flush();
            }
            protected override void Dispose(bool disposing)
            {
                OwnerThreads.Add(Thread.CurrentThread.ManagedThreadId);
                WasDisposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class GatedStream : MemoryStream
        {
            internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            internal bool Fail;

            public override void Write(byte[] buffer, int offset, int count)
            {
                Entered.Set();
                Release.Wait();
                if (Fail) throw new IOException("write failed");
                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Entered.Set();
                Release.Wait();
                if (Fail) throw new IOException("write failed");
                base.Write(buffer);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_slow_peer_cannot_grow_the_queue_by_frame_count_or_bytes(bool largeFrames)
        {
            using var wire = new GatedStream();
            var aborted = 0;
            TransportResponseWriter writer = null;
            writer = new TransportResponseWriter(new StreamWriter(wire, new UTF8Encoding(false), 1024, leaveOpen: true), () =>
            {
                Interlocked.Increment(ref aborted);
                wire.Fail = true;
                wire.Release.Set();
            });
            try
            {
                writer.WriteLine("blocked frame");
                Assert.True(wire.Entered.Wait(TimeSpan.FromSeconds(2)));
                var frame = largeFrames ? new string('x', 1024 * 1024) : "small";
                for (var i = 0; i < 300; i++) writer.WriteLine(frame);
                Assert.True(aborted > 0);
                var executions = 0;
                var request = Request("after-overflow");
                writer.Dispatch(request, request.ToString(), (_, __) => executions++);
                Assert.Equal(0, executions);
            }
            finally { wire.Release.Set(); writer.Dispose(); }
        }

        [Fact]
        public void Dispatch_capacity_aborts_outside_the_queue_lock_and_does_not_execute_more_requests()
        {
            PendingRequestLifecycle.SetHostBusy(false);
            using var wire = new MemoryStream();
            TransportResponseWriter writer = null;
            var reentered = false;
            writer = new TransportResponseWriter(new StreamWriter(wire, new UTF8Encoding(false), 1024, leaveOpen: true), () =>
            {
                reentered = Task.Run(() => writer.WriteLine("late")).Wait(TimeSpan.FromSeconds(1));
            });
            var pending = new List<TaskCompletionSource<string>>();
            try
            {
                for (var i = 0; i < 257; i++)
                {
                    var request = Request("capacity-" + i);
                    writer.Dispatch(request, request.ToString(), (_, tcs) => pending.Add(tcs));
                }
                Assert.Equal(256, pending.Count);
                Assert.True(reentered);
            }
            finally
            {
                foreach (var tcs in pending) tcs.TrySetResult("{}");
                writer.Dispose();
            }
        }

        [Fact]
        public async Task Concurrent_dispose_is_bounded_when_the_peer_does_not_read()
        {
            using var wire = new GatedStream();
            var writer = new TransportResponseWriter(new StreamWriter(wire, new UTF8Encoding(false), 1024, leaveOpen: true));
            writer.WriteLine("blocked frame");
            Assert.True(wire.Entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(writer.IsIdleFor(TimeSpan.Zero), "An outstanding write is not an idle connection.");
            try
            {
                var disposing = Task.WhenAll(Task.Run(() => writer.Dispose()), Task.Run(() => writer.Dispose()));
                await disposing.WaitAsync(TransportResponseWriter.DrainTimeout + TimeSpan.FromSeconds(2));
                writer.WriteLine("late frame");
            }
            finally
            {
                wire.Release.Set();
                writer.Dispose();
            }
            Assert.Equal("blocked frame" + Environment.NewLine, Encoding.UTF8.GetString(wire.ToArray()));
        }

        [Fact]
        public async Task Dispose_does_not_wait_for_execution_and_late_completion_is_safe()
        {
            PendingRequestLifecycle.SetHostBusy(false);
            using var wire = new MemoryStream();
            var writer = new TransportResponseWriter(new StreamWriter(wire, new UTF8Encoding(false), 1024, leaveOpen: true));
            TaskCompletionSource<string> execution = null;
            var request = Request("late");
            writer.Dispatch(request, request.ToString(), (line, tcs) => execution = tcs);
            Assert.Equal(1, writer.InFlight);

            await Task.Run(() => writer.Dispose()).WaitAsync(TimeSpan.FromSeconds(2));
            execution.TrySetResult("{\"id\":\"late\",\"success\":true}");
            await WaitUntil(() => writer.InFlight == 0);
            writer.Dispose();
            Assert.Empty(wire.ToArray());
        }

        [Fact]
        public async Task Write_failure_aborts_the_connection_and_rejects_late_frames()
        {
            using var wire = new GatedStream { Fail = true };
            wire.Release.Set();
            var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = 0;
            var writer = new TransportResponseWriter(new StreamWriter(wire, new UTF8Encoding(false), 1024, leaveOpen: true), () =>
            {
                Interlocked.Increment(ref failures);
                failed.TrySetResult(true);
            });
            writer.WriteLine("failed frame");
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            writer.WriteLine("late frame");
            await Task.WhenAll(Task.Run(() => writer.Dispose()), Task.Run(() => writer.Dispose()));
            Assert.Equal(1, failures);
            Assert.Empty(wire.ToArray());
        }
    }
}
