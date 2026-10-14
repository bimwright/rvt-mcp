using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;
using ServerAuthToken = RvtMcp.Server.AuthToken;
using PluginAuthToken = RvtMcp.Plugin.AuthToken;
using TargetException = RvtMcp.Server.TargetException;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class GatewayFailureIntegrationTests : IDisposable
    {
        private const int Pid = 474747;
        private static readonly DateTime Start = new DateTime(2026, 10, 8, 8, 0, 0, DateTimeKind.Utc);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-gateway-failure-" + Guid.NewGuid().ToString("N"));
        private readonly string _oldDir = ServerAuthToken.DiscoveryDirOverride;
        private readonly RvtMcpConfig _oldConfig = ServerState.Config;
        private readonly RvtMcp.ToolCatalog.ToolCatalog _oldCatalog = ServerState.ToolCatalog;
        private readonly Func<string, object, int?, Task<JObject>> _oldSend = ToolGateway.SendOverride;
        private readonly ChangeHistoryStore _oldHistory = ToolGateway.History;
        private readonly Func<FailureLedgerStore> _oldLedger = FailureTools.StoreFactory;
        private readonly FakeProbe _probe = new FakeProbe();
        private readonly RvtMcpConfig _config = new RvtMcpConfig { Toolsets = new List<string> { "all" }, EnableChangeHistory = false };
        private readonly FailureLedgerStore _ledger;

        public GatewayFailureIntegrationTests()
        {
            Directory.CreateDirectory(_root);
            ServerAuthToken.DiscoveryDirOverride = _root;
            ToolGateway.ResetForTests();
            ToolGateway.SendOverride = null;
            ToolGateway.History = new ChangeHistoryStore(Path.Combine(_root, "history"));
            ServerState.Config = _config;
            ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(_config), _config);
            RevitTargetBinding.ResetForTests(_probe);
            _ledger = new FailureLedgerStore(Path.Combine(_root, "ledger.db"));
            FailureTools.StoreFactory = () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true);
        }

        public void Dispose()
        {
            ToolGateway.ResetForTests();
            RevitTargetBinding.ResetForTests();
            ServerAuthToken.DiscoveryDirOverride = _oldDir;
            ServerState.Config = _oldConfig;
            ServerState.ToolCatalog = _oldCatalog;
            ToolGateway.SendOverride = _oldSend;
            ToolGateway.History = _oldHistory;
            FailureTools.StoreFactory = _oldLedger;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class FakeProbe : IProcessProbe
        {
            public ProcessProbeResult Probe(int pid) => pid == Pid || pid == Pid + 1
                ? new ProcessProbeResult(ProcessState.Running, Start, "Test Revit")
                : new ProcessProbeResult(ProcessState.NotFound, null, null);
        }

        private sealed class WireHost : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly object _gate = new object();
            private TcpClient _client;
            private StreamWriter _writer;
            public int Port { get; }
            public bool CloseHandshake;
            public Action<JObject, WireHost> OnCommand;
            public readonly ConcurrentQueue<JObject> Requests = new ConcurrentQueue<JObject>();
            private readonly int _identityPid;

            public WireHost(int identityPid = Pid)
            {
                _identityPid = identityPid;
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                new Thread(Serve) { IsBackground = true }.Start();
            }

            private void Serve()
            {
                try
                {
                    while (true)
                    {
                        using var client = _listener.AcceptTcpClient();
                        try
                        {
                            _client = client;
                            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                            lock (_gate) _writer = writer;
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                var request = JObject.Parse(line);
                                if (request.Value<string>("command") == "set_tool_catalog")
                                {
                                    if (CloseHandshake) { client.Close(); break; }
                                    Send(Final(request));
                                    continue;
                                }
                                Requests.Enqueue(request);
                                if (OnCommand == null) Send(Final(request));
                                else OnCommand(request, this);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            public void Send(JObject response)
            {
                if (!(response["_target"] is JObject)) response["_target"] = new JObject
                {
                    ["target_id"] = "revit-2024-" + _identityPid, ["pid"] = _identityPid, ["host_year"] = 2024,
                    ["document_id"] = "doc-" + _identityPid
                };
                lock (_gate) _writer.WriteLine(response.ToString(Formatting.None));
            }
            public void CloseClient() => _client?.Close();
            public void Dispose() { _listener.Stop(); CloseClient(); }
        }

        private void Descriptor(int port, bool protocol = true, int pid = Pid, string token = "test-token", string pipe = null, bool prepareBinding = true)
        {
            var descriptor = new JObject
            {
                ["schema_version"] = 3, ["host_app"] = "revit", ["host_year"] = 2024,
                ["pid"] = pid, ["process_start_utc"] = Start.ToString("o"), ["target_id"] = "revit-2024-" + pid,
                ["transport"] = pipe == null ? "tcp" : "pipe", ["port"] = pipe == null ? (JToken)port : null,
                ["pipe_name"] = pipe, ["auth_token"] = token,
                ["capabilities"] = protocol ? new JArray("tool_catalog", "controlled_failures_v1", "document_target_v1") : new JArray("tool_catalog")
            };
            var path = Path.Combine(_root, "revit-2024-" + pid + ".json");
            File.WriteAllText(path + ".tmp", descriptor.ToString(Formatting.None));
            File.Move(path + ".tmp", path, overwrite: true);
            // Failure-protocol cases exercise requests after target/document verification.
            // Unconfirmed first writes are covered separately by ToolGatewayBindingTests.
            if (prepareBinding && RevitTargetBinding.Snapshot().State == BindingState.None)
            {
                RevitTargetBinding.Switch(TargetSelector.ForPid(pid), RevitTargetBinding.Scan().Live);
                RevitTargetBinding.Binding.ConfirmDocument(RevitTargetBinding.CaptureGeneration(), "doc-" + pid);
            }
        }

        private static JObject Final(JObject request) => new JObject
        {
            ["id"] = request["id"], ["success"] = true, ["data"] = new JObject { ["viewName"] = "Test View" },
            ["_operation"] = request["operation_id"] == null ? null : new JObject
            {
                ["operationId"] = request["operation_id"], ["execution"] = "completed", ["outcome"] = "committed",
                ["complete"] = true, ["mutationApplied"] = true
            }
        };

        private static async Task<JObject> Send(string command = "get_current_view_info", int? timeout = null)
        {
            var context = TargetCallContext.Begin();
            try { return await ToolGateway.SendToRevit(command, timeoutSeconds: timeout); }
            finally { TargetCallContext.End(context); }
        }

        // What the agent receives: the tool result after the call filter, which attaches finished operations.
        private JObject Delivered(JObject result, string tool = "revit_get_current_view_info") =>
            JObject.Parse(((TextContentBlock)RuntimeToolFilter.Apply(new CallToolRequestParams { Name = tool },
                new CallToolResult { Content = new[] { new TextContentBlock { Text = result.ToString() } } }, _config,
                previousOperations: ToolGateway.PreviousOperations).Content[0]).Text);

        private static ConcurrentDictionary<string, GatewayPendingRequest> Pending =>
            (ConcurrentDictionary<string, GatewayPendingRequest>)typeof(ToolGateway).GetField("_pending", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        private static long CurrentEpoch()
        {
            var connection = typeof(ToolGateway).GetField("_conn", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            return (long)connection.GetType().GetField("Epoch").GetValue(connection);
        }

        private async Task WithMcp(Func<McpClient, CancellationToken, Task> run)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var name = "rvt-gateway-mcp-" + Guid.NewGuid().ToString("N");
            using var input = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var output = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(input.WaitForConnectionAsync(deadline.Token), output.ConnectAsync(deadline.Token));
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            var mcp = builder.Services.AddMcpServer().WithStreamServerTransport(input, input);
            mcp = RuntimeToolFilter.Register(mcp, _config, new SessionContext(false, Path.Combine(_root, "session")));
            Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(_config), _config);
            using var host = builder.Build();
            await host.StartAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(output, output), cancellationToken: deadline.Token);
            await run(client, deadline.Token);
            await client.DisposeAsync();
            await host.StopAsync(deadline.Token);
        }

        private static JObject Text(CallToolResult result) => JObject.Parse(((TextContentBlock)result.Content[0]).Text);

        [Theory]
        [InlineData("eof")]
        [InlineData("switch")]
        [InlineData("not_written")]
        public async Task MCP_interruption_and_known_not_written_preserve_target_and_operation(string mode)
        {
            using var wire = new WireHost();
            using var replacement = new WireHost(Pid + 1);
            var notWritten = mode == "not_written";
            Descriptor(wire.Port);
            if (notWritten) ToolGateway.BeforeWriteForTests = ToolGateway.CloseConnection;
            else wire.OnCommand = (_, host) =>
            {
                if (mode == "eof") host.CloseClient();
                else
                {
                    Descriptor(replacement.Port, pid: Pid + 1);
                    ToolGateway.SwitchBinding(TargetSelector.ForPid(Pid + 1), RevitTargetBinding.Scan().Live, rebind: true);
                }
            };
            await WithMcp(async (client, ct) =>
            {
                var response = await client.CallToolAsync("revit_get_current_view_info", cancellationToken: ct);
                var envelope = Text(response);
                Assert.True(response.IsError == true);
                Assert.Equal(notWritten ? "TARGET_UNAVAILABLE" : "TARGET_INTERRUPTED", envelope["target"]["code"]);
                Assert.Equal(!notWritten, envelope["target"].Value<bool>("sent"));
                Assert.Equal(notWritten ? "not_started" : "unknown", envelope["_operation"]["outcome"]);
                Assert.Equal(notWritten ? "completed" : "interrupted_unknown", envelope["_operation"]["execution"]);
                Assert.Equal(notWritten ? JTokenType.Boolean : JTokenType.Null, envelope["_operation"]["mutationApplied"].Type);
                Assert.False(string.IsNullOrWhiteSpace(envelope["_operation"].Value<string>("operationId")));
            });
            Assert.Equal(notWritten ? 0 : 1, wire.Requests.Count);
        }

        [Fact]
        public async Task Timeout_without_intermediate_frame_retains_final_history_and_native_status()
        {
            _config.EnableChangeHistory = true;
            using var wire = new WireHost();
            Descriptor(wire.Port);
            var dispatched = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            wire.OnCommand = (request, _) => dispatched.TrySetResult(request);
            var call = Send(timeout: 1);
            var request = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var operationId = request.Value<string>("operation_id");
            Assert.Equal(operationId, request["history"].Value<string>("Id"));
            Assert.Equal(1, request.Value<int>("failure_protocol"));
            var capture = ChangeHistoryTests.Capture();
            capture["callId"] = operationId;
            capture["session"] = request["history"]["Session"];
            var final = Final(request);
            final["history_transfer"] = ToolGateway.History.Transfer.Publish(operationId, capture);
            var pending = Pending[request.Value<string>("id")];
            Assert.True(HistoryCallLease.IsActive(operationId));
            ToolGateway.History.RecoverPending();
            Assert.Single(ToolGateway.History.Transfer.PendingFiles());

            var receipt = await call.WaitAsync(TimeSpan.FromSeconds(9));
            Assert.Equal("timed_out_running", receipt["_operation"]["execution"]);
            // No dialog was reported, so the timeout must not read as one.
            Assert.Equal("REVIT_OPERATION_TIMED_OUT", receipt["error"]);
            Assert.Equal(FailureCapture.TimedOutNextStep, receipt["_operation"]["nextStep"]);
            Assert.False(pending.Execution.Task.IsCompleted);
            Assert.True(HistoryCallLease.IsActive(operationId));
            _ledger.SaveOperation(new FailureOperation { OperationId = operationId, Execution = "completed", Outcome = "committed", MutationApplied = true, FinalResult = final });
            wire.Send(final);
            await pending.Execution.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(HistoryCallLease.IsActive(operationId));
            Assert.Equal("recorded", pending.HistoryReceipt["status"]);
            Assert.Equal("timed_out_running", JObject.Parse(await pending.Caller.Task)["_operation"]["execution"]);
            Assert.Equal("committed", JObject.Parse(await FailureTools.GetOperationStatus(operationId))["outcome"]);
            Assert.Single(ToolGateway.History.Query(capture["activeModel"].Value<string>("key"))["calls"]);
            Assert.Empty(ToolGateway.History.Transfer.PendingFiles());
            Assert.False(ToolGateway.RouteResponse(pending.ConnectionId, final));
            Assert.Equal("recorded", pending.HistoryReceipt["status"]);
        }

        [Fact]
        public async Task Immediate_final_wins_and_records_history_in_caller_context_once()
        {
            _config.EnableChangeHistory = true;
            using var wire = new WireHost();
            Descriptor(wire.Port);
            JObject capture = null;
            wire.OnCommand = (request, host) =>
            {
                capture = ChangeHistoryTests.Capture();
                capture["callId"] = request["operation_id"];
                capture["session"] = request["history"]["Session"];
                var final = Final(request);
                final["history_transfer"] = ToolGateway.History.Transfer.Publish(request.Value<string>("operation_id"), capture);
                host.Send(final);
            };
            using var context = new ChangeCaptureContext();
            var data = await Send(timeout: 1);
            Assert.Equal("Test View", data["viewName"]);
            Assert.Null(data["success"]); // Successful data is not a wire envelope.
            Assert.Equal("recorded", context.History["status"]);
            Assert.Single(ToolGateway.History.Query(capture["activeModel"].Value<string>("key"))["calls"]);
            Assert.Empty(ToolGateway.History.Transfer.PendingFiles());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Dialog_wait_names_the_dialog_busy_calls_point_at_it_and_the_final_reaches_the_agent_once(bool readStatus)
        {
            using var wire = new WireHost();
            Descriptor(wire.Port);
            JObject first = null;
            wire.OnCommand = (request, host) =>
            {
                if (first != null) { host.Send(Final(request)); return; }
                first = request;
                host.Send(new JObject
                {
                    ["id"] = request["id"], ["frame"] = "event", ["event"] = "dialog_wait",
                    ["_operation"] = new JObject { ["operationId"] = request["operation_id"], ["execution"] = "waiting_for_user", ["outcome"] = "unknown",
                        ["complete"] = false, ["mutationApplied"] = null,
                        ["dialog"] = new JObject { ["code"] = "TaskDialog_Test_Sample", ["kind"] = "TaskDialog", ["text"] = "Sample text", ["inCatalog"] = false } }
                });
            };
            var receipt = await Send("open_model");
            var operationId = receipt["_operation"].Value<string>("operationId");
            Assert.Equal("TaskDialog_Test_Sample", receipt["_operation"]["dialog"]["code"]);

            var blocked = await Send("create_wall");
            Assert.StartsWith("REVIT_BUSY:", blocked.Value<string>("error"));
            Assert.Equal(operationId, blocked["_operation"]["blockedBy"]["operationId"]);
            Assert.Equal("Sample text", blocked["_operation"]["blockedBy"]["dialog"]["text"]);
            Assert.Null(Delivered(blocked, "revit_create_wall")["_previousOperation"]);

            var pending = Pending[first.Value<string>("id")];
            var final = Final(first);
            wire.Send(final);
            await pending.Execution.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (readStatus)
            {
                _ledger.SaveOperation(new FailureOperation { OperationId = operationId, Execution = "completed", Outcome = "committed", MutationApplied = true, FinalResult = final });
                var status = JObject.Parse(await FailureTools.GetOperationStatus(operationId));
                Assert.Equal("Test View", status["finalResult"]["data"]["viewName"]);
            }

            var next = Delivered(await Send());
            if (readStatus) Assert.Null(next["_previousOperation"]);
            else
            {
                var previous = Assert.Single((JArray)next["_previousOperation"]);
                Assert.Equal(operationId, previous["operationId"]);
                Assert.Equal("revit_open_model", previous["tool"]);
                Assert.Equal("committed", previous["outcome"]);
            }
            Assert.Null(Delivered(await Send())["_previousOperation"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Dialog_receipt_then_EOF_or_switch_cannot_replace_caller_or_native_outcome(bool switchTarget)
        {
            using var wire = new WireHost();
            Descriptor(wire.Port);
            var dispatched = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            wire.OnCommand = (request, host) =>
            {
                dispatched.TrySetResult(request);
                host.Send(new JObject { ["id"] = request["id"], ["frame"] = "event", ["event"] = "dialog_wait" });
            };
            var receipt = await Send();
            var request = await dispatched.Task;
            var pending = Pending[request.Value<string>("id")];
            var callerResponse = await pending.Caller.Task;
            Assert.Equal("waiting_for_user", receipt["_operation"]["execution"]);
            var blocked = await Send("create_wall");
            Assert.StartsWith("REVIT_BUSY:", blocked.Value<string>("error"));
            Assert.Equal("not_started", blocked["_operation"]["outcome"]);
            Assert.Single(wire.Requests);
            if (switchTarget)
            {
                using var replacement = new WireHost(Pid + 1);
                Descriptor(replacement.Port, pid: Pid + 1);
                Assert.True(ToolGateway.SwitchBinding(TargetSelector.ForPid(Pid + 1), RevitTargetBinding.Scan().Live, rebind: true).Ok);
            }
            else wire.CloseClient();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.Execution.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            _ledger.SaveOperation(new FailureOperation { OperationId = request.Value<string>("operation_id"), Execution = "completed", Outcome = "rolled_back", MutationApplied = false });
            Assert.Equal(callerResponse, await pending.Caller.Task);
            Assert.Equal("rolled_back", JObject.Parse(await FailureTools.GetOperationStatus(receipt["_operation"].Value<string>("operationId")))["outcome"]);
        }

        [Fact]
        public async Task Reconnect_retry_uses_new_capability_and_actual_dispatch_epoch_and_ignores_old_frames()
        {
            using var first = new WireHost();
            using var second = new WireHost();
            Descriptor(first.Port, protocol: false);
            await Send();
            var oldEpoch = CurrentEpoch();
            var generation = RevitTargetBinding.CaptureGeneration();
            var dispatched = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            second.OnCommand = (request, _) => dispatched.TrySetResult(request);
            var attempts = 0;
            ToolGateway.BeforeWriteForTests = () =>
            {
                if (++attempts != 1) return;
                ToolGateway.CloseConnection();
                Descriptor(second.Port);
            };
            var call = Send("create_wall");
            var request = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pending = Pending[request.Value<string>("id")];
            Assert.True(pending.ConnectionId > oldEpoch);
            Assert.Equal(generation, RevitTargetBinding.Snapshot().Generation);
            var final = Final(request);
            Assert.False(ToolGateway.RouteResponse(oldEpoch, final));
            Assert.False(call.IsCompleted);
            second.Send(final);
            Assert.Equal("committed", (await call.WaitAsync(TimeSpan.FromSeconds(3)))["_operation"]["outcome"]);
            Assert.Equal(2, attempts);
            Assert.Single(first.Requests);
            Assert.Single(second.Requests);
        }

        [Fact]
        public async Task Closed_handshake_is_unavailable_not_a_synthetic_operation_answer()
        {
            using var wire = new WireHost { CloseHandshake = true };
            Descriptor(wire.Port, prepareBinding: false);
            var error = await Assert.ThrowsAsync<TargetException>(() => Send());
            Assert.Equal(TargetCode.TargetUnavailable, error.Payload.Code);
            Assert.Equal("not_started", error.Operation["outcome"]);
            Assert.Equal(BindingState.None, RevitTargetBinding.Snapshot().State);
            Assert.Empty(wire.Requests);
        }

        [Theory]
        [InlineData("success", false)]
        [InlineData("pending", true)]
        [InlineData("busy", true)]
        [InlineData("failure", true)]
        [InlineData("error", true)]
        [InlineData("skip", false)]
        public async Task MCP_switch_verification_preserves_binding_but_marks_unsuccessful_probe_as_error(string kind, bool isError)
        {
            using var wire = new WireHost();
            Descriptor(wire.Port);
            wire.OnCommand = (request, host) =>
            {
                var frame = Final(request);
                if (kind != "success")
                {
                    frame["success"] = false;
                    frame["error"] = kind == "busy" ? "REVIT_BUSY" : "Probe failed";
                    frame["_operation"] = kind == "error" ? null : new JObject
                    {
                        ["operationId"] = request["operation_id"], ["execution"] = kind == "pending" ? "timed_out_running" : "completed",
                        ["outcome"] = kind == "pending" ? "unknown" : "not_started", ["complete"] = kind != "pending"
                    };
                    if (kind == "failure") frame["_failure"] = new JObject { ["operationId"] = request["operation_id"], ["gate"] = "blocked" };
                }
                if (kind == "error") frame.Remove("_operation"); // Legacy failure has no safety receipt.
                host.Send(frame);
            };
            await WithMcp(async (client, ct) =>
            {
                var response = await client.CallToolAsync("revit_switch_target",
                    new Dictionary<string, object> { ["version"] = "pid:" + Pid, ["verify"] = kind != "skip" }, cancellationToken: ct);
                var envelope = Text(response);
                Assert.Equal(!isError, envelope.Value<bool>("ok"));
                Assert.Equal(kind == "success", envelope.Value<bool>("verified"));
                Assert.Equal(isError, response.IsError == true);
                Assert.Equal(Pid, envelope["binding"].Value<int>("bound_pid"));
                if (kind == "pending" || kind == "busy" || kind == "failure")
                    Assert.Equal(Assert.Single(wire.Requests)["operation_id"], envelope["_operation"]["operationId"]);
                if (kind == "failure") Assert.Equal("blocked", envelope["_failure"]["gate"]);
            });
            if (kind == "skip") Assert.Empty(wire.Requests);
        }

        [Theory]
        [InlineData("success", true)]
        [InlineData("pending", false)]
        [InlineData("busy", false)]
        [InlineData("failure", false)]
        [InlineData("error", false)]
        public async Task Switch_verification_distinguishes_data_from_failure_or_pending(string kind, bool verified)
        {
            using var wire = new WireHost();
            Descriptor(wire.Port);
            wire.OnCommand = (request, host) =>
            {
                var frame = Final(request);
                if (kind != "success")
                {
                    frame["success"] = false;
                    frame["error"] = kind == "busy" ? "REVIT_BUSY" : "Probe failed";
                    frame["_operation"] = kind == "error" ? null : new JObject
                    {
                        ["operationId"] = request["operation_id"], ["execution"] = kind == "pending" ? "timed_out_running" : "completed",
                        ["outcome"] = kind == "pending" ? "unknown" : "not_started", ["complete"] = kind != "pending"
                    };
                    if (kind == "failure") frame["_failure"] = new JObject { ["operationId"] = request["operation_id"], ["gate"] = "blocked" };
                }
                else frame["_userDialogs"] = new JObject { ["model"] = "Sample", ["items"] = new JArray(), ["note"] = UserDialogBuffer.Note };
                if (kind == "error") frame.Remove("_operation"); // A legacy error has no operation receipt.
                host.Send(frame);
            };
            var context = TargetCallContext.Begin();
            try
            {
                var response = JObject.Parse(await MetaTools.SwitchTarget("pid:" + Pid, verify: true));
                Assert.Equal(verified, response.Value<bool>("ok"));
                Assert.Equal(verified, response.Value<bool>("verified"));
                Assert.Null(context.Payload);
                if (kind == "pending" || kind == "busy" || kind == "failure")
                    Assert.Equal(Assert.Single(wire.Requests)["operation_id"], response["_operation"]["operationId"]);
                if (kind == "failure") Assert.Equal("blocked", response["_failure"]["gate"]);
                if (kind == "success")
                {
                    // The verify probe is the call that receives this once-only report; it must not be lost there.
                    Assert.Equal("Sample", response["_userDialogs"]["model"]);
                }
            }
            finally { TargetCallContext.End(context); }
        }

        [Fact]
        public async Task Timed_out_read_has_session_status_and_a_status_wait_returns_when_it_finishes()
        {
            using var wire = new WireHost();
            Descriptor(wire.Port);
            JObject first = null;
            wire.OnCommand = (request, host) =>
            {
                if (first != null) { host.Send(Final(request)); return; }
                first = request;
                // The add-in's own timeout receipt; reads have no ledger record.
                host.Send(new JObject { ["id"] = request["id"], ["success"] = false, ["error"] = "REVIT_OPERATION_TIMED_OUT",
                    ["_operation"] = new JObject { ["operationId"] = request["operation_id"], ["execution"] = "timed_out_running",
                        ["outcome"] = "unknown", ["complete"] = false, ["mutationApplied"] = null } });
            };
            var receipt = await Send("list_sheets");
            var operationId = receipt["_operation"].Value<string>("operationId");

            var status = JObject.Parse(await FailureTools.GetOperationStatus(operationId));
            Assert.True(status.Value<bool>("available"));
            Assert.Equal("gateway_session", status["source"]);
            Assert.Equal("timed_out_running", status["execution"]);
            Assert.Null(status["waitedSeconds"]);

            var expired = JObject.Parse(await FailureTools.GetOperationStatus(operationId, waitSeconds: 1));
            Assert.True(expired.Value<bool>("stillWaiting"));
            Assert.True(expired.Value<double>("waitedSeconds") >= 1);

            var waiting = FailureTools.GetOperationStatus(operationId, waitSeconds: 30);
            await Task.Delay(200);
            Assert.False(waiting.IsCompleted);
            wire.Send(Final(first));
            var finished = JObject.Parse(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("completed", finished["execution"]);
            Assert.Equal("Test View", finished["finalResult"]["data"]["viewName"]);
            Assert.True(finished.Value<double>("waitedSeconds") < 30);
            Assert.Null(finished["stillWaiting"]);

            // Read once by status: the next response does not repeat it, and status still answers.
            Assert.Null(Delivered(await Send())["_previousOperation"]);
            Assert.Equal("completed", JObject.Parse(await FailureTools.GetOperationStatus(operationId))["execution"]);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Ledger_status_keeps_the_sessions_bounded_user_report_before_marking_it_read(bool summaryFirst, bool oversized)
        {
            using var wire = new WireHost();
            Descriptor(wire.Port);
            JObject first = null;
            wire.OnCommand = (request, host) =>
            {
                if (first != null) { host.Send(Final(request)); return; }
                first = request;
                host.Send(new JObject { ["id"] = request["id"], ["frame"] = "event", ["event"] = "dialog_wait",
                    ["_operation"] = new JObject { ["operationId"] = request["operation_id"],
                        ["execution"] = "waiting_for_user", ["complete"] = false } });
            };
            var receipt = await Send("open_model");
            var operationId = receipt["_operation"].Value<string>("operationId");
            var pending = Pending[first.Value<string>("id")];
            var final = Final(first);
            final["_userDialogs"] = new JObject { ["model"] = "Sample", ["items"] = new JArray(
                new JObject { ["code"] = "OutsideMcp", ["text"] = oversized ? new string('漢', 3000) : "Already closed." }) };
            _ledger.SaveOperation(new FailureOperation { OperationId = operationId, Execution = "completed",
                Outcome = "committed", MutationApplied = true, FinalResult = final });
            Assert.Null(_ledger.GetOperation(operationId).FinalResult["_userDialogs"]); // Ephemeral, never written to the ledger.
            wire.Send(final);
            await pending.Execution.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (summaryFirst)
                Assert.Equal("Sample", Assert.Single((JArray)Delivered(await Send())["_previousOperation"])["_userDialogs"]["model"]);

            var status = JObject.Parse(await FailureTools.GetOperationStatus(operationId));
            Assert.Equal("Test View", status["finalResult"]["data"]["viewName"]);
            if (oversized) Assert.Null(status["finalResult"]["_userDialogs"]);
            else Assert.Equal("OutsideMcp", status["finalResult"]["_userDialogs"]?["items"]?[0]?["code"]);
            Assert.Null(Delivered(await Send())["_previousOperation"]);
        }

        [Fact]
        public async Task Status_wait_follows_the_ledger_when_the_session_does_not_hold_the_request()
        {
            var interval = FailureTools.WaitPollInterval;
            FailureTools.WaitPollInterval = TimeSpan.FromMilliseconds(50);
            try
            {
                _ledger.SaveOperation(new FailureOperation { OperationId = "ledger-op", Execution = "waiting_for_user", Outcome = "unknown" });
                var waiting = FailureTools.GetOperationStatus("ledger-op", waitSeconds: 30);
                await Task.Delay(200);
                Assert.False(waiting.IsCompleted);
                _ledger.SaveOperation(new FailureOperation { OperationId = "ledger-op", Execution = "completed", Outcome = "committed", MutationApplied = true });
                var finished = JObject.Parse(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("committed", finished["outcome"]);
                Assert.Null(finished["stillWaiting"]);
                // Nothing to wait for: an unknown operation answers at once.
                var unknown = JObject.Parse(await FailureTools.GetOperationStatus("unknown-op", waitSeconds: 30).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.False(unknown.Value<bool>("available"));
            }
            finally { FailureTools.WaitPollInterval = interval; }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(91)]
        public async Task Status_wait_is_bounded(int waitSeconds)
        {
            var response = JObject.Parse(await FailureTools.GetOperationStatus("any-op", waitSeconds));
            Assert.Equal("INVALID_ARGUMENT", response["error"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Oversized_result_suggests_a_narrower_request_only_for_a_read(bool isRead)
        {
            var finished = new TaskCompletionSource<string>();
            finished.SetResult(new JObject { ["success"] = true, ["data"] = new JObject { ["rows"] = new string('x', PreviousOperationTracker.MaxDataBytes) } }.ToString());
            ToolGateway.PreviousOperations.Remember("big-op", "revit_list_sheets", finished.Task, "timed_out_running", isRead);
            var data = ToolGateway.PreviousOperations.Status("big-op")["finalResult"]["data"];
            Assert.True(data.Value<bool>("omitted"));
            Assert.Equal(isRead, data.Value<string>("reason").Contains("narrower"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Real_transport_token_rejection_refreshes_descriptor_and_retries(bool pipe)
        {
            var oldYear = PluginAuthToken.RevitVersion;
            PluginAuthToken.RevitVersion = "2024";
            var nativeDir = Path.Combine(_root, "native");
            Directory.CreateDirectory(nativeDir);
            var pipeName = pipe ? "rvt-gateway-auth-" + Guid.NewGuid().ToString("N") : null;
            var port = 0;
            var connections = 0;
            Action<string> log = message =>
            {
                if (message != "Client connected") return;
                Interlocked.Increment(ref connections);
                Descriptor(port, token: PluginAuthToken.Current, pipe: pipeName);
            };
            using ITransportServer transport = pipe
                ? new PipeTransportServer(nativeDir, log, pipeName)
                : new TcpTransportServer(nativeDir, log);
            try
            {
                var dispatched = 0;
                transport.Start((line, completion) =>
                {
                    Interlocked.Increment(ref dispatched);
                    completion.SetResult(Final(JObject.Parse(line)).ToString(Formatting.None));
                });
                if (transport is TcpTransportServer tcp) port = tcp.Port;
                Descriptor(port, token: "stale-token", pipe: pipeName);
                var data = await Send().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("Test View", data["viewName"]);
                Assert.Equal(2, connections);
                Assert.Equal(1, dispatched);
            }
            finally { ToolGateway.CloseConnection(); transport.Stop(); PluginAuthToken.RevitVersion = oldYear; }
        }
    }
}
