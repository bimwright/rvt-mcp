using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class TransportAuthenticationTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Rejected_token_delivers_unauthorized_before_eof_and_allows_authenticated_reconnect(bool usePipe)
        {
            var directory = Path.Combine(Path.GetTempPath(), "rvtmcp-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipeName = "rvtmcp-auth-" + Guid.NewGuid().ToString("N");
            using ITransportServer server = usePipe
                ? new PipeTransportServer(directory, _ => { }, pipeName)
                : new TcpTransportServer(directory, _ => { });
            var callbacks = 0;
            try
            {
                server.Start((line, tcs) =>
                {
                    Interlocked.Increment(ref callbacks);
                    tcs.TrySetResult("{\"success\":true}");
                });
                // Repeat the enqueue-and-close path on fresh real connections, not a synchronous fake host.
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    using var connection = await Connect(server, usePipe, pipeName);
                    using var reader = new StreamReader(connection, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var writer = new StreamWriter(connection, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    var id = "rejected-" + attempt;
                    try { await writer.WriteLineAsync(CatalogRequest(id, "invalid-token").ToString(Formatting.None)).WaitAsync(TimeSpan.FromSeconds(3)); }
                    finally
                    {
                        // The server deliberately closes this connection; a final pipe flush may see it broken.
                        try { writer.Dispose(); } catch (IOException) { }
                    }
                    var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.NotNull(line);
                    var response = JObject.Parse(line);
                    Assert.Equal(id, response.Value<string>("id"));
                    Assert.False(response.Value<bool>("success"));
                    Assert.StartsWith("Unauthorized:", response.Value<string>("error"));
                    Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                }

                using var authenticated = await Connect(server, usePipe, pipeName);
                using var authenticatedReader = new StreamReader(authenticated, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var authenticatedWriter = new StreamWriter(authenticated, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                await authenticatedWriter.WriteLineAsync(CatalogRequest("accepted", AuthToken.Current).ToString(Formatting.None)).WaitAsync(TimeSpan.FromSeconds(3));
                var accepted = JObject.Parse(await authenticatedReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal("accepted", accepted.Value<string>("id"));
                Assert.True(accepted.Value<bool>("success"));
                Assert.Equal(0, callbacks); // Auth and catalog are transport control-plane messages.
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static JObject CatalogRequest(string id, string token) => new JObject
        {
            ["id"] = id, ["token"] = token, ["command"] = "set_tool_catalog",
            ["params"] = new JObject
            {
                ["schema_version"] = 1, ["server_version"] = "1.1.0",
                ["catalogued_built_in_count"] = 0, ["tools"] = new JArray()
            }
        };

        [Fact]
        public async Task Pipe_terminal_drain_releases_a_nonreading_client_within_the_deadline()
        {
            var directory = Path.Combine(Path.GetTempPath(), "rvtmcp-drain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipeName = "rvtmcp-drain-" + Guid.NewGuid().ToString("N");
            var rejected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new PipeTransportServer(directory, message =>
            {
                if (message == "Auth rejected") rejected.TrySetResult(true);
            }, pipeName);
            try
            {
                server.Start((line, tcs) => tcs.TrySetResult("{\"success\":true}"));
                using var nonreading = await Connect(server, true, pipeName);
                using var writer = new StreamWriter(nonreading, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(CatalogRequest("rejected", "invalid-token").ToString(Formatting.None)).WaitAsync(TimeSpan.FromSeconds(3));
                await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var released = Task.Run(() => SpinWait.SpinUntil(() => !server.IsClientConnected, TimeSpan.FromSeconds(3)));
                Assert.True(await released.WaitAsync(TimeSpan.FromSeconds(4)), "A non-reading peer must not hold the pipe listener indefinitely.");

                using var next = await Connect(server, true, pipeName);
                using var nextWriter = new StreamWriter(next, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var nextReader = new StreamReader(next, Encoding.UTF8, false, 1024, leaveOpen: true);
                await nextWriter.WriteLineAsync(CatalogRequest("next", AuthToken.Current).ToString(Formatting.None)).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(JObject.Parse(await nextReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3))).Value<bool>("success"));
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static async Task<Stream> Connect(ITransportServer server, bool usePipe, string pipeName)
        {
            if (usePipe)
            {
                var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await client.ConnectAsync(3000); return client; }
                catch { client.Dispose(); throw; }
            }
            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, ((TcpTransportServer)server).Port).WaitAsync(TimeSpan.FromSeconds(3));
                return tcp.GetStream(); // Disposing the owning NetworkStream also closes the socket.
            }
            catch { tcp.Dispose(); throw; }
        }
    }
}
