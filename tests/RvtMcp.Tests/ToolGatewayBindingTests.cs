using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;
using ServerAuthToken = RvtMcp.Server.AuthToken;

namespace RvtMcp.Tests
{
    /// <summary>
    /// ToolGateway target-binding flow against in-process fake listeners
    /// (spec §6.4–§6.7): busy vs unavailable, token re-read, descriptor-retry,
    /// mid-command interruption, generation pinning vs switch.
    /// </summary>
    [Collection("ServerStateConfig")]
    public class ToolGatewayBindingTests : IDisposable
    {
        private const int PidA = 424242;
        private const int PidB = 434343;
        private static readonly DateTime StartA = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime StartB = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);

        private readonly string _dir;
        private readonly FakeProbe _probe = new FakeProbe();
        private readonly string _originalDirOverride;
        private readonly Func<string, object, int?, Task<JObject>> _originalSend;
        private readonly RvtMcp.ToolCatalog.ToolCatalog _originalCatalog;
        private readonly RvtMcpConfig _originalConfig;

        public ToolGatewayBindingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "rvtmcp-gw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _originalDirOverride = ServerAuthToken.DiscoveryDirOverride;
            _originalSend = ToolGateway.SendOverride;
            _originalCatalog = ServerState.ToolCatalog;
            _originalConfig = ServerState.Config;

            ServerAuthToken.DiscoveryDirOverride = _dir;
            RevitTargetBinding.ResetForTests(_probe);
            ToolGateway.SendOverride = null;
            ToolGateway.ResetForTests();
            var config = new RvtMcpConfig();
            ServerState.Config = config;
            ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
        }

        public void Dispose()
        {
            ServerAuthToken.DiscoveryDirOverride = _originalDirOverride;
            ToolGateway.SendOverride = _originalSend;
            ServerState.ToolCatalog = _originalCatalog;
            ServerState.Config = _originalConfig;
            RevitTargetBinding.ResetForTests();
            ToolGateway.ResetForTests();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private sealed class FakeProbe : IProcessProbe
        {
            private readonly Dictionary<int, ProcessProbeResult> _byPid = new();
            public void Set(int pid, ProcessState state, DateTime? start = null, string title = null)
                => _byPid[pid] = new ProcessProbeResult(state, start, title);
            public ProcessProbeResult Probe(int pid)
                => _byPid.TryGetValue(pid, out var r) ? r : new ProcessProbeResult(ProcessState.NotFound, null, null);
        }

        /// <summary>In-process TCP listener speaking the NDJSON wire protocol.</summary>
        private sealed class FakeTcpHost : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;

            public int Port { get; }
            public List<string> Commands { get; } = new List<string>();
            public int Handshakes;
            public int Connections;
            public bool AnswerHandshake = true;
            public bool CloseOnCommand;
            public string RequiredToken;
            /// <summary>Set by the wire command "__park__" (after its response is sent)
            /// — the host holds the socket open but stops reading, so a large client
            /// request fills the send buffer and blocks in WriteLine.</summary>
            public volatile bool StopReading;
            public volatile bool Disposed;
            /// <summary>Invoked after a rejected handshake is written — lets the test rotate the descriptor.</summary>
            public Action OnRejected;

            public FakeTcpHost()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _thread = new Thread(AcceptLoop) { IsBackground = true };
                _thread.Start();
            }

            private void AcceptLoop()
            {
                try
                {
                    while (true)
                    {
                        var client = _listener.AcceptTcpClient();
                        Interlocked.Increment(ref Connections);
                        HandleClient(client);
                    }
                }
                catch { }
            }

            private void HandleClient(TcpClient client)
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                    {
                        string line;
                        while (true)
                        {
                            if (StopReading)
                            {
                                while (!Disposed && StopReading) Thread.Sleep(50);
                                if (Disposed) return;
                            }
                            line = reader.ReadLine();
                            if (line == null) break;
                            var req = JObject.Parse(line);
                            var id = req.Value<string>("id");
                            var command = req.Value<string>("command");
                            if (command == "set_tool_catalog")
                            {
                                Interlocked.Increment(ref Handshakes);
                                if (RequiredToken != null && req.Value<string>("token") != RequiredToken)
                                {
                                    OnRejected?.Invoke(); // rotate the descriptor before the reject is seen
                                    writer.WriteLine(new JObject { ["id"] = id, ["success"] = false, ["error"] = "Unauthorized: invalid token" }.ToString(Formatting.None));
                                    continue;
                                }
                                if (AnswerHandshake)
                                    writer.WriteLine(new JObject { ["id"] = id, ["success"] = true, ["data"] = new JObject() }.ToString(Formatting.None));
                                continue;
                            }
                            lock (Commands) Commands.Add(command);
                            if (CloseOnCommand) return; // close stream → client sees EOF mid-request
                            writer.WriteLine(new JObject { ["id"] = id, ["success"] = true, ["data"] = new JObject { ["ok"] = true } }.ToString(Formatting.None));
                            if (command == "__park__") StopReading = true; // park before the next read
                        }
                    }
                }
                catch { }
            }

            public void Dispose()
            {
                Disposed = true;
                try { _listener.Stop(); } catch { }
            }
        }

        /// <summary>Single-client named-pipe fake host.</summary>
        private sealed class FakePipeHost : IDisposable
        {
            private NamedPipeServerStream _pipe;
            private readonly Thread _thread;
            public string Name { get; } = "RvtMcp-test-" + Guid.NewGuid().ToString("N");
            public List<string> Commands { get; } = new List<string>();

            public FakePipeHost()
            {
                _pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            private void Serve()
            {
                try
                {
                    while (true)
                    {
                        var pipe = _pipe;
                        pipe.WaitForConnection();
                        try
                        {
                            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true))
                            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true })
                            {
                                string line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    var req = JObject.Parse(line);
                                    var id = req.Value<string>("id");
                                    var command = req.Value<string>("command");
                                    if (command != "set_tool_catalog")
                                        lock (Commands) Commands.Add(command);
                                    writer.WriteLine(new JObject { ["id"] = id, ["success"] = true, ["data"] = new JObject { ["ok"] = true } }.ToString(Formatting.None));
                                }
                            }
                        }
                        catch { }
                        try { pipe.Disconnect(); } catch { }
                        pipe.Dispose();
                        _pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    }
                }
                catch { }
            }

            public void Dispose()
            {
                try { _pipe?.Dispose(); } catch { }
            }
        }

        private void WritePerInstanceDescriptor(int pid, int year, DateTime start, string transport, int? port, string pipeName, string token)
        {
            var json = new JObject
            {
                ["schema_version"] = 3,
                ["host_app"] = "revit",
                ["host_year"] = year,
                ["pid"] = pid,
                ["process_start_utc"] = start.ToString("o"),
                ["target_id"] = $"revit-{year}-{pid}",
                ["transport"] = transport,
                ["port"] = transport == "tcp" ? port : null,
                ["pipe_name"] = transport == "pipe" ? pipeName : null,
                ["auth_token"] = token,
                ["capabilities"] = new JArray("tool_catalog"),
            };
            var path = Path.Combine(_dir, $"revit-{year}-{pid}.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json.ToString(Formatting.None));
            File.Move(tmp, path, overwrite: true); // atomic, like the plugin publisher
        }

        private void DeleteDescriptor(int pid, int year) =>
            File.Delete(Path.Combine(_dir, $"revit-{year}-{pid}.json"));

        private static TargetCallContext BeginCall()
            => TargetCallContext.Begin();

        private async Task<JObject> Send(string command)
        {
            var ctx = BeginCall();
            try { return await ToolGateway.SendToRevit(command); }
            finally { TargetCallContext.End(ctx); }
        }

        private async Task<TargetException> SendExpectingFailure(string command, int? timeoutSeconds = null)
        {
            var ctx = BeginCall();
            try
            {
                await ToolGateway.SendToRevit(command, null, timeoutSeconds);
                throw new Xunit.Sdk.XunitException("expected TargetException");
            }
            catch (TargetException ex) { return ex; }
            finally { TargetCallContext.End(ctx); }
        }

        // ---- §6.4 connect outcomes ----

        [Fact]
        public async Task First_call_binds_and_routes_command_to_live_target()
        {
            using var host = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-a");

            var data = await Send("get_current_view_info");

            Assert.True(data.Value<bool>("ok"));
            Assert.Equal(1, host.Handshakes);
            Assert.Equal(new[] { "get_current_view_info" }, host.Commands);
            var snap = RevitTargetBinding.Snapshot();
            Assert.Equal(BindingState.Bound, snap.State);
            Assert.Equal(PidA, snap.Record.Pid);
            Assert.Equal(1, snap.Generation);
            Assert.Equal("revit-2024-" + PidA, ToolGateway.ConnectedTargetId);
        }

        [Fact]
        public async Task Busy_handshake_timeout_reports_target_busy_without_sending()
        {
            using var host = new FakeTcpHost { AnswerHandshake = false };
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-a");

            var sw = Stopwatch.StartNew();
            var ex = await SendExpectingFailure("get_element_details");
            sw.Stop();

            Assert.Equal(TargetCode.TargetBusy, ex.Payload.Code);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9), "elapsed " + sw.Elapsed);
            Assert.Empty(host.Commands); // command never written
            Assert.Equal(BindingState.None, RevitTargetBinding.Snapshot().State); // no binding on failed first connect
        }

        [Fact]
        public async Task No_listener_reports_target_unavailable()
        {
            var unusedPort = new TcpListener(IPAddress.Loopback, 0);
            unusedPort.Start();
            var port = ((IPEndPoint)unusedPort.LocalEndpoint).Port;
            unusedPort.Stop();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", port, null, "tok-a");

            var ex = await SendExpectingFailure("get_element_details");

            Assert.Equal(TargetCode.TargetUnavailable, ex.Payload.Code);
            Assert.False(ex.Payload.Sent);
        }

        [Fact]
        public async Task Missing_pipe_reports_target_unavailable()
        {
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2026, StartA, "pipe", null, "RvtMcp-nonexistent-" + Guid.NewGuid().ToString("N"), "tok-a");

            var ex = await SendExpectingFailure("get_element_details");

            Assert.Equal(TargetCode.TargetUnavailable, ex.Payload.Code);
        }

        [Fact]
        public async Task Token_rejection_triggers_one_descriptor_reread_and_retry()
        {
            using var host = new FakeTcpHost { RequiredToken = "tok-new" };
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-old");
            host.OnRejected = () => WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-new");

            var data = await Send("get_current_view_info");

            Assert.True(data.Value<bool>("ok"));
            Assert.Equal(2, host.Handshakes); // rejected once, retried with the fresh token
            Assert.Equal(new[] { "get_current_view_info" }, host.Commands);
        }

        // ---- reconnect / descriptor retry (§6.4 step 2) ----

        [Fact]
        public async Task Missing_descriptor_during_reconnect_keeps_binding_and_fails_unavailable()
        {
            using var host = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-a");
            await Send("get_current_view_info");
            Assert.Equal(BindingState.Bound, RevitTargetBinding.Snapshot().State);

            host.Dispose();            // connection dies
            ToolGateway.ResetForTests();
            DeleteDescriptor(PidA, 2024); // descriptor gone (restart window)

            var sw = Stopwatch.StartNew();
            var ex = await SendExpectingFailure("get_element_details");
            sw.Stop();

            Assert.Equal(TargetCode.TargetUnavailable, ex.Payload.Code);
            Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.5), "should retry ~2s, elapsed " + sw.Elapsed);
            var snap = RevitTargetBinding.Snapshot();
            Assert.Equal(BindingState.Bound, snap.State); // binding kept
            Assert.Equal(1, snap.Generation);
        }

        [Fact]
        public async Task Descriptor_reappearing_within_retry_window_recovers()
        {
            var host1 = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host1.Port, null, "tok-a");
            await Send("get_current_view_info");

            host1.Dispose();
            ToolGateway.ResetForTests();
            DeleteDescriptor(PidA, 2024);

            // Descriptor (with a fresh port) comes back inside the 2 s window.
            var host2 = new FakeTcpHost();
            using var restored = host2;
            var bringBack = Task.Run(async () =>
            {
                await Task.Delay(400);
                WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host2.Port, null, "tok-a");
            });

            var data = await Send("get_element_details");
            await bringBack;

            Assert.True(data.Value<bool>("ok"));
            Assert.Equal(new[] { "get_element_details" }, host2.Commands);
        }

        // ---- §6.7 interruption ----

        [Fact]
        public async Task Connection_closed_mid_command_fails_interrupted_immediately()
        {
            using var host = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-a");
            await Send("get_current_view_info"); // bound + connected
            host.CloseOnCommand = true; // drop the socket on the next real command

            var sw = Stopwatch.StartNew();
            var ex = await SendExpectingFailure("get_element_details", timeoutSeconds: 60);
            sw.Stop();

            Assert.Equal(TargetCode.TargetInterrupted, ex.Payload.Code);
            Assert.True(ex.Payload.Sent);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "elapsed " + sw.Elapsed);
            Assert.Equal(new[] { "get_current_view_info", "get_element_details" }, host.Commands);
        }

        // ---- generation pinning vs switch ----

        [Fact]
        public async Task Switch_between_capture_and_send_is_never_written()
        {
            using var hostA = new FakeTcpHost();
            using var hostB = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            _probe.Set(PidB, ProcessState.Running, StartB, "Revit B");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", hostA.Port, null, "tok-a");
            WritePerInstanceDescriptor(PidB, 2024, StartB, "tcp", hostB.Port, null, "tok-b");

            await Send("get_current_view_info");
            var boundPid = RevitTargetBinding.Snapshot().Record.Pid;
            var otherPid = boundPid == PidA ? PidB : PidA;
            var otherHost = boundPid == PidA ? hostB : hostA;
            var boundHost = boundPid == PidA ? hostA : hostB;

            var ctx = BeginCall(); // pins the generation established by the first call
            try
            {
                var live = RevitTargetBinding.Scan().Live;
                var switched = RevitTargetBinding.Switch(TargetSelector.ForPid(otherPid), live);
                Assert.True(switched.Ok && switched.InstanceChanged);
                ToolGateway.CloseConnection();

                try
                {
                    await ToolGateway.SendToRevit("get_element_details");
                    throw new Xunit.Sdk.XunitException("expected TargetException");
                }
                catch (TargetException ex)
                {
                    Assert.Equal(TargetCode.TargetChanged, ex.Payload.Code);
                    Assert.False(ex.Payload.Sent);
                }
            }
            finally { TargetCallContext.End(ctx); }

            // Neither host saw the command — the other host because the send failed
            // before connect, the old host because the generation check precedes the write.
            Assert.Empty(boundHost.Commands.Where(c => c != "get_current_view_info").ToList());
            Assert.Empty(otherHost.Commands);
            Assert.Equal(0, otherHost.Handshakes);
        }

        // ---- pipe transport end-to-end ----

        [Fact]
        public async Task Pipe_transport_connects_handshakes_and_routes()
        {
            using var host = new FakePipeHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2026, StartA, "pipe", null, host.Name, "tok-a");

            var data = await Send("get_current_view_info");

            Assert.True(data.Value<bool>("ok"));
            Assert.Equal(new[] { "get_current_view_info" }, host.Commands);
        }

        [Fact]
        public async Task Occupied_single_instance_pipe_reports_target_busy()
        {
            var name = "RvtMcp-test-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var holder = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            var connectTask = server.WaitForConnectionAsync();
            holder.Connect(2000);
            await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2026, StartA, "pipe", null, name, "tok-a");

            var sw = Stopwatch.StartNew();
            var ex = await SendExpectingFailure("get_element_details");
            sw.Stop();

            Assert.Equal(TargetCode.TargetBusy, ex.Payload.Code);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9), "elapsed " + sw.Elapsed);
        }

        // ---- background callers (§6.4 no-context rule) ----

        [Fact]
        public async Task Call_outside_tool_context_never_first_binds()
        {
            using var host = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-a");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ToolGateway.SendToRevit("get_current_view_info"));

            Assert.Contains("No Revit target is bound", ex.Message);
            Assert.Equal(0, host.Handshakes); // never even connected
            Assert.Equal(BindingState.None, RevitTargetBinding.Snapshot().State);
        }

        [Fact]
        public async Task Implicit_binding_lost_with_no_candidates_reports_no_target()
        {
            using var host = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-a");
            await Send("get_current_view_info"); // implicit (auto) binding

            _probe.Set(PidA, ProcessState.Exited, StartA); // process died
            host.Dispose();
            ToolGateway.ResetForTests();

            var ex = await SendExpectingFailure("get_element_details");
            Assert.Equal(TargetCode.NoTarget, ex.Payload.Code); // §6.5 implicit + 0 candidates
            Assert.Equal(BindingState.Lost, RevitTargetBinding.Snapshot().State);
        }

        [Fact]
        public async Task Explicit_binding_lost_reports_target_lost()
        {
            using var host = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-a");
            RevitTargetBinding.ResetForTests(_probe, TargetSelector.ForPid(PidA)); // explicit pin
            await Send("get_current_view_info");

            _probe.Set(PidA, ProcessState.Exited, StartA);
            host.Dispose();
            ToolGateway.ResetForTests();

            var ex = await SendExpectingFailure("get_element_details");
            Assert.Equal(TargetCode.TargetLost, ex.Payload.Code);
            Assert.Equal(BindingState.Lost, RevitTargetBinding.Snapshot().State);
        }

        // ---- switch vs connection ownership (§6.6, §6.8) ----

        [Fact]
        public async Task Switch_closes_old_connection_before_returning_and_new_calls_miss_old_listener()
        {
            using var hostA = new FakeTcpHost();
            using var hostB = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            _probe.Set(PidB, ProcessState.Running, StartB, "Revit B");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", hostA.Port, null, "tok-a");
            WritePerInstanceDescriptor(PidB, 2024, StartB, "tcp", hostB.Port, null, "tok-b");
            await Send("get_current_view_info");
            var boundPid = RevitTargetBinding.Snapshot().Record.Pid;
            var otherPid = boundPid == PidA ? PidB : PidA;
            var oldHost = boundPid == PidA ? hostA : hostB;
            var newHost = boundPid == PidA ? hostB : hostA;

            var live = RevitTargetBinding.Scan().Live;
            var result = ToolGateway.SwitchBinding(TargetSelector.ForPid(otherPid), live);

            Assert.True(result.Ok && result.InstanceChanged);
            Assert.Null(ToolGateway.ConnectedTargetId); // closed atomically with the switch
            Assert.Equal(2, RevitTargetBinding.Snapshot().Generation);

            // A call captured under the new generation must never reach the old listener.
            var data = await Send("get_element_details");
            Assert.True(data.Value<bool>("ok"));
            Assert.Equal(new[] { "get_current_view_info" }, oldHost.Commands);
            Assert.Equal(new[] { "get_element_details" }, newHost.Commands);
            Assert.Equal("revit-2024-" + otherPid, ToolGateway.ConnectedTargetId);
        }

        [Fact]
        public async Task Stale_connection_from_earlier_generation_is_never_written()
        {
            using var hostA = new FakeTcpHost();
            using var hostB = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            _probe.Set(PidB, ProcessState.Running, StartB, "Revit B");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", hostA.Port, null, "tok-a");
            WritePerInstanceDescriptor(PidB, 2024, StartB, "tcp", hostB.Port, null, "tok-b");
            await Send("get_current_view_info");
            var boundPid = RevitTargetBinding.Snapshot().Record.Pid;
            var otherPid = boundPid == PidA ? PidB : PidA;
            var oldHost = boundPid == PidA ? hostA : hostB;
            var newHost = boundPid == PidA ? hostB : hostA;

            // Simulate the pre-fix race window: the binding moved to generation 2
            // while the open connection still serves generation 1.
            var live = RevitTargetBinding.Scan().Live;
            var switched = RevitTargetBinding.Switch(TargetSelector.ForPid(otherPid), live);
            Assert.True(switched.Ok && switched.InstanceChanged);
            Assert.Equal("revit-2024-" + boundPid, ToolGateway.ConnectedTargetId);

            var data = await Send("get_element_details"); // pinned generation 2

            Assert.True(data.Value<bool>("ok"));
            Assert.Equal(new[] { "get_current_view_info" }, oldHost.Commands); // stale conn never written
            Assert.Equal(new[] { "get_element_details" }, newHost.Commands);
            Assert.Equal("revit-2024-" + otherPid, ToolGateway.ConnectedTargetId);
        }

        [Fact]
        public async Task Call_captured_in_none_then_switch_is_target_changed_and_sent_nowhere()
        {
            using var hostA = new FakeTcpHost();
            using var hostB = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            _probe.Set(PidB, ProcessState.Running, StartB, "Revit B");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", hostA.Port, null, "tok-a");
            WritePerInstanceDescriptor(PidB, 2024, StartB, "tcp", hostB.Port, null, "tok-b");

            var victimCtx = BeginCall(); // binding none → pinned 0
            try
            {
                await Send("get_current_view_info"); // another call binds (generation 1)
                var boundPid = RevitTargetBinding.Snapshot().Record.Pid;
                var otherPid = boundPid == PidA ? PidB : PidA;
                var live = RevitTargetBinding.Scan().Live;
                var switched = ToolGateway.SwitchBinding(TargetSelector.ForPid(otherPid), live); // generation 2
                Assert.True(switched.Ok && switched.InstanceChanged);

                var ex = await Assert.ThrowsAsync<TargetException>(
                    () => ToolGateway.SendToRevit("get_element_details"));
                Assert.Equal(TargetCode.TargetChanged, ex.Payload.Code);
                Assert.False(ex.Payload.Sent);
            }
            finally { TargetCallContext.End(victimCtx); }

            // The victim's command reached neither listener.
            Assert.Equal(1, hostA.Commands.Count + hostB.Commands.Count);
            Assert.Equal("get_current_view_info",
                hostA.Commands.Count == 1 ? hostA.Commands[0] : hostB.Commands[0]);
        }

        [Fact]
        public async Task Parallel_first_calls_share_a_single_connection()
        {
            using var host = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", host.Port, null, "tok-a");

            var tasks = Enumerable.Range(0, 5)
                .Select(_ => Task.Run(() => Send("get_element_details")))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, d => Assert.True(d.Value<bool>("ok")));
            Assert.Equal(1, host.Connections); // the single-client listener saw exactly one socket
            Assert.Equal(1, host.Handshakes);
            Assert.Equal(5, host.Commands.Count);
            Assert.Equal(1, RevitTargetBinding.Snapshot().Generation);
        }

        [Fact]
        public async Task Switch_verify_failure_preserves_success_response_with_verify_target()
        {
            using var hostA = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", hostA.Port, null, "tok-a");
            await Send("get_current_view_info"); // only A is live → bound A

            // B is live but has no listener — the verify probe must fail.
            var dead = new TcpListener(IPAddress.Loopback, 0);
            dead.Start();
            var deadPort = ((IPEndPoint)dead.LocalEndpoint).Port;
            dead.Stop();
            _probe.Set(PidB, ProcessState.Running, StartB, "Revit B");
            WritePerInstanceDescriptor(PidB, 2024, StartB, "tcp", deadPort, null, "tok-b");

            var ctx = BeginCall();
            string text;
            try
            {
                text = await MetaTools.SwitchTarget("pid:" + PidB, verify: true);
                Assert.Null(ctx.Payload); // the filter must not replace the switch response
            }
            finally { TargetCallContext.End(ctx); }

            var json = JObject.Parse(text);
            Assert.True(json.Value<bool>("ok"));
            Assert.False(json.Value<bool>("verified"));
            Assert.Equal("TARGET_UNAVAILABLE", json["verify_target"]?.Value<string>("code"));
            Assert.Contains("TARGET_UNAVAILABLE", json.Value<string>("verifyError"));
            Assert.Equal(PidB, RevitTargetBinding.Snapshot().Record.Pid); // the switch itself held
            Assert.Equal(new[] { "get_current_view_info" }, hostA.Commands);
        }

        // ---- write-path contention (§6.6) ----

        [Fact]
        public async Task Blocked_wire_write_does_not_block_switch_target()
        {
            // Host A handshakes then never reads: a large request's WriteLine must
            // block on a full send buffer. switch_target takes only _connectLock, so
            // it must complete promptly and abort the stuck write via stream close.
            using var hostA = new FakeTcpHost();
            using var hostB = new FakeTcpHost();
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            _probe.Set(PidB, ProcessState.Running, StartB, "Revit B");
            WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", hostA.Port, null, "tok-a");
            WritePerInstanceDescriptor(PidB, 2024, StartB, "tcp", hostB.Port, null, "tok-b");
            RevitTargetBinding.ResetForTests(_probe, TargetSelector.ForPid(PidA)); // pin A explicitly
            await Send("get_current_view_info"); // bound + connected to A
            Assert.Equal("revit-2024-" + PidA, ToolGateway.ConnectedTargetId);
            await Send("__park__"); // host answers, then stops reading — like a busy plugin

            var bigParams = new { payload = new string('x', 16 * 1024 * 1024) };
            JObject returned = null;
            Exception other = null;
            var sendTask = Task.Run(async () =>
            {
                var ctx = BeginCall();
                try
                {
                    returned = await ToolGateway.SendToRevit("get_element_details", bigParams, 60);
                    return (TargetException)null;
                }
                catch (TargetException ex) { return ex; }
                catch (Exception ex) { other = ex; return (TargetException)null; }
                finally { TargetCallContext.End(ctx); }
            });
            // Give the writer time to fill the socket buffers and block inside WriteLine.
            await Task.Delay(1500);

            var sw = Stopwatch.StartNew();
            var live = RevitTargetBinding.Scan().Live;
            var result = ToolGateway.SwitchBinding(TargetSelector.ForPid(PidB), live);
            sw.Stop();

            Assert.True(result.Ok && result.InstanceChanged);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
                "switch_target blocked " + sw.ElapsedMilliseconds + "ms behind the stuck write");

            var ex = await sendTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(ex != null,
                "expected TargetException; returned=" + (returned?.ToString(Newtonsoft.Json.Formatting.None) ?? "null")
                + " other=" + (other?.ToString() ?? "null"));
            Assert.Equal(TargetCode.TargetInterrupted, ex.Payload.Code);
            Assert.True(ex.Payload.Sent); // a possibly-partial write is never sent:false (§6.7)
        }

        [Fact]
        public async Task Listener_closing_mid_handshake_reports_unavailable_not_busy()
        {
            // Only a handshake *timeout* is TARGET_BUSY; a connection that dies during
            // the handshake is TARGET_UNAVAILABLE (§6.4 busy-vs-unavailable).
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var closer = new Thread(() =>
            {
                try { while (true) { var c = listener.AcceptTcpClient(); c.Close(); } }
                catch { }
            }) { IsBackground = true };
            closer.Start();
            try
            {
                _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
                WritePerInstanceDescriptor(PidA, 2024, StartA, "tcp", port, null, "tok-a");

                var sw = Stopwatch.StartNew();
                var ex = await SendExpectingFailure("get_element_details");
                sw.Stop();

                Assert.Equal(TargetCode.TargetUnavailable, ex.Payload.Code);
                Assert.False(ex.Payload.Sent);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9),
                    "should not wait out the handshake budget, elapsed " + sw.Elapsed);
            }
            finally { listener.Stop(); }
        }
    }
}
