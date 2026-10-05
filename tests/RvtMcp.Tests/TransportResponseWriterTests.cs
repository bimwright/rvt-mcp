using System;
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
    }
}
