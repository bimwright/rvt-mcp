using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
    public sealed class TransportStopTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtmcp-test-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _logs = new List<string>();
        private readonly object _logGate = new object();

        public TransportStopTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private Action<string> Sink => m => { lock (_logGate) _logs.Add(m); };
        private int ErrorLines { get { lock (_logGate) return _logs.Count(l => l.Contains("Listen error")); } }
        private int LogLines { get { lock (_logGate) return _logs.Count; } }

        private static void RespondOk(string line, TaskCompletionSource<string> tcs) =>
            tcs.TrySetResult("{\"success\":true}");

        private string DescriptorPath()
        {
            var pid = Process.GetCurrentProcess().Id;
            return Path.Combine(_dir, $"revit-2022-{pid}.json");
        }

        [Fact]
        public async Task Pipe_Stop_Idempotent_ClosesClient_DeletesDescriptor()
        {
            var pipeName = "rvtmcp-test-" + Guid.NewGuid().ToString("N");
            var server = new PipeTransportServer(_dir, Sink, pipeName);
            server.Start(RespondOk);
            Assert.True(File.Exists(DescriptorPath()));

            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            client.Connect(5000);
            Authenticate(client);
            Assert.True(server.IsClientConnected || SpinWait.SpinUntil(() => server.IsClientConnected, 2000));

            var sw = Stopwatch.StartNew();
            server.Stop();
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stop took {sw.ElapsedMilliseconds} ms");

            // client read returns EOF or throws within ~2 s
            var read = Task.Run(() =>
            {
                try { return client.ReadByte(); }
                catch (IOException) { return -1; }
                catch (ObjectDisposedException) { return -1; }
            });
            var completed = await Task.WhenAny(read, Task.Delay(2500));
            Assert.Same(read, completed);
            Assert.Equal(-1, await read);

            server.Stop();   // idempotent
            server.Dispose();
            Assert.False(File.Exists(DescriptorPath())); // own descriptor cleaned up
            Assert.Contains(_logs, l => l.Contains("Stopped"));
            client.Dispose();
        }

        [Fact]
        public async Task Tcp_Stop_Idempotent_ClosesClient_DeletesDescriptor()
        {
            var server = new TcpTransportServer(_dir, Sink);
            server.Start(RespondOk);
            Assert.True(File.Exists(DescriptorPath()));

            var client = new TcpClient();
            client.Connect("127.0.0.1", server.Port);
            Authenticate(client.GetStream());
            Assert.True(SpinWait.SpinUntil(() => server.IsClientConnected, 2000));

            var sw = Stopwatch.StartNew();
            server.Stop();
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stop took {sw.ElapsedMilliseconds} ms");

            var read = Task.Run(() =>
            {
                try { return client.GetStream().ReadByte(); }
                catch (IOException) { return -1; }
                catch (SocketException) { return -1; }
                catch (ObjectDisposedException) { return -1; }
            });
            var completed = await Task.WhenAny(read, Task.Delay(2500));
            Assert.Same(read, completed);
            Assert.Equal(-1, await read);

            server.Stop();
            server.Dispose();
            Assert.False(File.Exists(DescriptorPath()));
            Assert.Contains(_logs, l => l.Contains("Stopped"));
            client.Dispose();
        }

        [Fact]
        public void Pipe_busy_start_is_explicit_then_a_fresh_listener_recovers()
        {
            var name = "rvtmcp-busy-" + Guid.NewGuid().ToString("N");
            using var holder = new NamedPipeServerStream(name, PipeDirection.InOut, 1);
            using var refused = new PipeTransportServer(_dir, Sink, name);
            Assert.Throws<IOException>(() => refused.Start(RespondOk));
            Assert.False(refused.IsRunning);
            Assert.False(File.Exists(DescriptorPath()));
            holder.Dispose();
            using var recovered = new PipeTransportServer(_dir, Sink, name);
            recovered.Start(RespondOk);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            client.Connect(5000);
            Authenticate(client);
            Assert.True(recovered.IsClientConnected);
        }

        private static void Authenticate(Stream stream)
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            writer.WriteLine(new JObject
            {
                ["id"] = "auth",
                ["token"] = AuthToken.Current,
                ["command"] = "set_tool_catalog",
                ["params"] = JObject.Parse("{\"schema_version\":1,\"server_version\":\"1.1.0\",\"catalogued_built_in_count\":0,\"tools\":[]}")
            }.ToString(Newtonsoft.Json.Formatting.None));
            Assert.True(JObject.Parse(reader.ReadLine()).Value<bool>("success"));
        }

        // ---- stop mid-request / restart with stale client ----

        private string SendValidRequest(StreamWriter writer, string id, string command)
        {
            var token = JObject.Parse(File.ReadAllText(DescriptorPath())).Value<string>("auth_token");
            writer.WriteLine(new JObject
            {
                ["id"] = id, ["token"] = token, ["command"] = command,
                ["params"] = new JObject(), ["timeout_seconds"] = 60,
                ["failure_protocol"] = 1, ["operation_id"] = Guid.NewGuid().ToString("N")
            }.ToString(Newtonsoft.Json.Formatting.None));
            return token;
        }

        private static async Task AssertClientEnded(Func<int> readByte)
        {
            var read = Task.Run(() =>
            {
                try { return readByte(); }
                catch (IOException) { return -1; }
                catch (SocketException) { return -1; }
                catch (ObjectDisposedException) { return -1; }
            });
            var completed = await Task.WhenAny(read, Task.Delay(5000));
            Assert.Same(read, completed);
            Assert.Equal(-1, await read);
        }

        [Fact]
        public async Task Tcp_Stop_While_Client_Mid_Request_Completes_Promptly()
        {
            // Revit execution never completes; Stop must not wait for it while draining the wire.
            TaskCompletionSource<string> pending = null;
            var server = new TcpTransportServer(_dir, Sink);
            server.Start((line, tcs) => pending = tcs);
            var client = new TcpClient();
            client.Connect("127.0.0.1", server.Port);
            Authenticate(client.GetStream());
            Assert.True(SpinWait.SpinUntil(() => server.IsClientConnected, 2000));
            var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            SendValidRequest(writer, "1", "slow_command");
            Assert.True(SpinWait.SpinUntil(() => pending != null, 3000), "request never reached the handler");

            var sw = Stopwatch.StartNew();
            server.Stop();
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stop took {sw.ElapsedMilliseconds} ms");
            await AssertClientEnded(() => client.GetStream().ReadByte());
            Assert.False(File.Exists(DescriptorPath()));

            // Completing the orphaned request afterwards must not throw server-side.
            pending?.TrySetResult("{\"id\":\"1\",\"success\":true}");
            server.Dispose();
            client.Dispose();
        }

        [Fact]
        public async Task Pipe_Stop_While_Client_Mid_Request_Completes_Promptly()
        {
            TaskCompletionSource<string> pending = null;
            var pipeName = "rvtmcp-test-" + Guid.NewGuid().ToString("N");
            var server = new PipeTransportServer(_dir, Sink, pipeName);
            server.Start((line, tcs) => pending = tcs);
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            client.Connect(5000);
            Authenticate(client);
            Assert.True(SpinWait.SpinUntil(() => server.IsClientConnected, 2000));
            var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            SendValidRequest(writer, "1", "slow_command");
            Assert.True(SpinWait.SpinUntil(() => pending != null, 3000), "request never reached the handler");

            var sw = Stopwatch.StartNew();
            server.Stop();
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stop took {sw.ElapsedMilliseconds} ms");
            await AssertClientEnded(() => client.ReadByte());
            Assert.False(File.Exists(DescriptorPath()));

            pending?.TrySetResult("{\"id\":\"1\",\"success\":true}");
            server.Dispose();
            client.Dispose();
        }

        [Fact]
        public async Task Tcp_Restart_Serves_New_Client_While_Stale_Client_Sees_End()
        {
            var server1 = new TcpTransportServer(_dir, Sink);
            server1.Start(RespondOk);
            var stale = new TcpClient();
            stale.Connect("127.0.0.1", server1.Port);
            Authenticate(stale.GetStream());
            Assert.True(SpinWait.SpinUntil(() => server1.IsClientConnected, 2000));

            server1.Stop();

            // The stale client is still open client-side: it must see the end of stream.
            await AssertClientEnded(() => stale.GetStream().ReadByte());

            // A fresh server instance on the same discovery dir serves a new client.
            var server2 = new TcpTransportServer(_dir, Sink);
            server2.Start(RespondOk);
            Assert.True(File.Exists(DescriptorPath())); // descriptor republished
            var fresh = new TcpClient();
            fresh.Connect("127.0.0.1", server2.Port);
            Authenticate(fresh.GetStream());
            Assert.True(SpinWait.SpinUntil(() => server2.IsClientConnected, 2000));
            var writer = new StreamWriter(fresh.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(fresh.GetStream(), Encoding.UTF8);
            SendValidRequest(writer, "9", "ping");
            var response = await Task.Run(() => reader.ReadLine());
            Assert.True(JObject.Parse(response).Value<bool>("success"));

            server2.Dispose();
            stale.Dispose();
            fresh.Dispose();
        }

        [Fact]
        public async Task Pipe_Restart_Serves_New_Client_While_Stale_Client_Sees_End()
        {
            var pipeName = "rvtmcp-test-" + Guid.NewGuid().ToString("N");
            var server1 = new PipeTransportServer(_dir, Sink, pipeName);
            server1.Start(RespondOk);
            var stale = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            stale.Connect(5000);
            Authenticate(stale);
            Assert.True(SpinWait.SpinUntil(() => server1.IsClientConnected, 2000));

            server1.Stop();
            await AssertClientEnded(() => stale.ReadByte());

            var server2 = new PipeTransportServer(_dir, Sink, pipeName);
            server2.Start(RespondOk);
            Assert.True(File.Exists(DescriptorPath()));
            var fresh = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            fresh.Connect(5000);
            Authenticate(fresh);
            Assert.True(SpinWait.SpinUntil(() => server2.IsClientConnected, 2000));
            var writer = new StreamWriter(fresh, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(fresh, Encoding.UTF8);
            SendValidRequest(writer, "9", "ping");
            var response = await Task.Run(() => reader.ReadLine());
            Assert.True(JObject.Parse(response).Value<bool>("success"));

            server2.Dispose();
            stale.Dispose();
            fresh.Dispose();
        }
    }
}
