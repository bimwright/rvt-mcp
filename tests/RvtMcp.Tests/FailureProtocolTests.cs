using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class FailureProtocolTests
    {
        [Fact]
        public async Task Event_then_late_final_keeps_correlation_and_actual_outcome()
        {
            var operation = Guid.NewGuid().ToString("N");
            var pending = new GatewayPendingRequest(operation, 1);
            Assert.False(pending.Route(new JObject { ["frame"] = "event", ["event"] = "dialog_wait", ["operationId"] = operation }));
            Assert.True(pending.WaitingForUser);
            Assert.False(pending.Execution.Task.IsCompleted);
            Assert.Contains("waiting_for_user", await pending.Caller.Task);
            var final = new JObject { ["success"] = false, ["error"] = "REVIT_FAILURE_ROLLED_BACK", ["_operation"] = new JObject { ["operationId"] = operation, ["execution"] = "completed", ["outcome"] = "partial" } };
            Assert.True(pending.Route(final));
            Assert.False(pending.WaitingForUser);
            Assert.Equal("partial", JObject.Parse(await pending.Execution.Task)["_operation"]["outcome"]);
        }

        [Fact]
        public void Unknown_event_never_completes_execution_or_caller()
        {
            var pending = new GatewayPendingRequest("op", 1);
            Assert.False(pending.Route(new JObject { ["frame"] = "event", ["event"] = "future_event", ["success"] = true }));
            Assert.False(pending.Caller.Task.IsCompleted);
            Assert.False(pending.Execution.Task.IsCompleted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Timeout_and_final_have_one_caller_owner_in_either_order(bool timeoutWins)
        {
            var pending = new GatewayPendingRequest("race-operation", 1);
            var final = new JObject { ["success"] = true, ["data"] = new JObject { ["value"] = 1 },
                ["_operation"] = new JObject { ["operationId"] = "race-operation", ["execution"] = "completed", ["outcome"] = "committed" } };
            if (timeoutWins) Assert.True(pending.Timeout());
            Assert.True(pending.Route(final));
            Assert.False(pending.Timeout());
            Assert.Equal(timeoutWins ? "timed_out_running" : "completed", JObject.Parse(await pending.Caller.Task)["_operation"]["execution"]);
            Assert.Equal("committed", JObject.Parse(await pending.Execution.Task)["_operation"]["outcome"]);
        }

        [Fact]
        public void Gateway_error_conversion_preserves_receipts()
        {
            var envelope = Envelope();
            var result = ToolGateway.InterpretResponse(envelope);
            Assert.False(result.Value<bool>("success"));
            Assert.Equal("blocked", result["_failure"]["gate"]);
            Assert.Equal("partial", result["_operation"]["outcome"]);
        }

        [Fact]
        public void Plugin_and_mcp_guards_preserve_failure_ids_states_and_errors()
        {
            var envelope = Envelope();
            envelope["data"] = new JObject { ["detail"] = new string('x', 200000) };
            var config = new RvtMcpConfig { ResponseBudgetBytes = 12000, ResponseStrongWarnBytes = 9000, ResponseWarnBytes = 4000, MaxResponseBytes = 20000 };
            var guarded = ResponseEnvelopeGuard.Apply("send_code_to_revit", "{}", envelope, config);
            Assert.False(guarded.Value<bool>("success"));
            Assert.Equal("REVIT_FAILURE_ROLLED_BACK", guarded["error"]);
            Assert.Equal("operation-1", guarded["_failure"]["operationId"]);
            var data = ToolGateway.InterpretResponse(envelope);
            data["details"] = new string('x', 200000);
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_send_code_to_revit" },
                new CallToolResult { Content = new[] { new TextContentBlock { Text = data.ToString() } }, IsError = true }, config);
            Assert.True(result.IsError);
            var payload = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
            Assert.Equal("REVIT_FAILURE_ROLLED_BACK", payload["error"]);
            Assert.Equal("partial", payload["_operation"]["outcome"]);
            Assert.Equal("blocked", payload["_failure"]["gate"]);
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= 12000);
        }

        [Fact]
        public void MCP_compaction_keeps_both_target_and_operation_safety_receipt()
        {
            var envelope = new JObject { ["success"] = false, ["error"] = "TARGET_INTERRUPTED",
                ["target"] = new JObject { ["code"] = "TARGET_INTERRUPTED", ["sent"] = true, ["outcome"] = "unknown",
                    ["previous"] = new JObject { ["window_title"] = new string('x', 100000) } },
                ["_operation"] = new JObject { ["operationId"] = "interrupted-op", ["execution"] = "interrupted_unknown",
                    ["outcome"] = "unknown", ["mutationApplied"] = JValue.CreateNull(), ["complete"] = false } };
            var config = new RvtMcpConfig { ResponseBudgetBytes = 12000, ResponseStrongWarnBytes = 9000, ResponseWarnBytes = 4000, MaxResponseBytes = 20000 };
            var response = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_create_wall" },
                new CallToolResult { Content = new[] { new TextContentBlock { Text = envelope.ToString() } } }, config);
            var payload = JObject.Parse(((TextContentBlock)response.Content[0]).Text);
            Assert.True(response.IsError == true);
            Assert.Equal("TARGET_INTERRUPTED", payload["target"]["code"]);
            Assert.Equal("interrupted-op", payload["_operation"]["operationId"]);
            Assert.Equal(JTokenType.Null, payload["_operation"]["mutationApplied"].Type);
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(response)) <= 12000);
        }

        [Fact]
        public void Summary_caps_groups_ids_and_unicode_bytes()
        {
            var receipt = Envelope()["_failure"] as JObject;
            receipt["groups"] = new JArray(Enumerable.Range(0, 25).Select(_ => new JObject { ["description"] = new string('漢', 4096), ["sampleElementIds"] = new JArray(Enumerable.Range(0, 30)) }));
            var bound = SafetyResponseMetadata.Bound(receipt, 16384);
            Assert.True(Encoding.UTF8.GetByteCount(bound.ToString()) < 16384);
            Assert.Equal("operation-1", bound["operationId"]);
            Assert.Equal("blocked", bound["gate"]);
        }

        [Fact]
        public void Local_queries_require_issued_context_and_do_not_dispatch_Revit()
        {
            Assert.Contains("INVALID_ARGUMENT", FailureTools.GetFailureRecords());
            Assert.Contains("INVALID_ARGUMENT", FailureTools.GetOperationStatus(@"C:\private\model.rvt"));
            var config = new RvtMcpConfig { ReadOnly = true, EnableSendCode = false, EnableChangeHistory = false };
            var catalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
            foreach (var name in new[] { "revit_get_failure_records", "revit_get_operation_status" })
                Assert.Equal("server_local", catalog.Tools.Single(t => t.McpName == name).Timeout.PolicyKind);
        }

        [Fact]
        public void Missing_ledger_queries_create_no_files_and_never_send_to_Revit()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-failure-query-" + Guid.NewGuid().ToString("N"));
            var factory = FailureTools.StoreFactory;
            var send = ToolGateway.SendOverride;
            try
            {
                FailureTools.StoreFactory = () => new FailureLedgerStore(Path.Combine(root, "ledger.db"), readOnly: true);
                ToolGateway.SendOverride = (_, __, ___) => throw new Exception("Local queries must not dispatch Revit.");
                Assert.False(JObject.Parse(FailureTools.GetFailureRecords(modelScopeKey: "model1")).Value<bool>("available"));
                Assert.False(JObject.Parse(FailureTools.GetOperationStatus("operation1")).Value<bool>("available"));
                Assert.False(Directory.Exists(root));
            }
            finally { FailureTools.StoreFactory = factory; ToolGateway.SendOverride = send; }
        }

        [Fact]
        public async Task Unpaired_mutation_is_rejected_before_callback()
        {
            using var stream = new MemoryStream();
            using var writer = new TransportResponseWriter(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
            var request = new JObject { ["id"] = "unpaired", ["command"] = "create_wall" };
            writer.Dispatch(request, request.ToString(), (_, __) => throw new Exception("Unpaired writes must not be dispatched."));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (stream.Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.Contains("PAIRING_REQUIRED", Encoding.UTF8.GetString(stream.ToArray()));
        }

        [Fact]
        public async Task Delayed_modal_busy_preserves_qualified_correlation_without_dialog_contents()
        {
            var payload = new JObject { ["correlation"] = "correlated_not_proven", ["operationId"] = "prior-op", ["modelScopeKey"] = "model1", ["dialogText"] = "private user dialog content" };
            PendingRequestLifecycle.SetHostBusy(true, payload);
            payload["operationId"] = "later-mutation";
            try
            {
                using var stream = new MemoryStream();
                using var writer = new TransportResponseWriter(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
                var request = new JObject { ["id"] = "blocked", ["operation_id"] = "new-op", ["command"] = "get_current_view_info" };
                writer.Dispatch(request, request.ToString(), (_, __) => throw new Exception("A modal host must not enqueue Revit work."));
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (stream.Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
                var json = Encoding.UTF8.GetString(stream.ToArray());
                var result = ToolGateway.InterpretResponse(JObject.Parse(json));
                Assert.Equal("correlated_not_proven", result["_operation"]["correlation"]);
                Assert.Equal("prior-op", result["_operation"]["correlatedOperationId"]);
                Assert.Equal("new-op", result["_operation"]["operationId"]);
                Assert.Equal("not_started", result["_operation"]["outcome"]);
                Assert.DoesNotContain("private", json);
            }
            finally { PendingRequestLifecycle.SetHostBusy(false); }
            Assert.Null(PendingRequestLifecycle.HostBusyMetadata);
        }

        [Fact]
        public async Task Transport_delivers_modal_event_busy_and_late_final_without_UI_wait()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var server = await accept;
                using var reader = new StreamReader(client.GetStream());
                using var writer = new TransportResponseWriter(new StreamWriter(server.GetStream(), new UTF8Encoding(false)) { AutoFlush = true });
                TaskCompletionSource<string> execution = null;
                var request = new JObject { ["id"] = "modal-1", ["command"] = "get_current_view_info", ["operation_id"] = Guid.NewGuid().ToString("N") };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                writer.Dispatch(request, request.ToString(), (line, completion) =>
                {
                    execution = completion;
                    var lifecycle = PendingRequestLifecycle.For(completion);
                    Assert.True(lifecycle.TryStart());
                    lifecycle.EmitDialogWait(new JObject { ["dialogId"] = "manual" });
                });
                var frame = JObject.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal("dialog_wait", frame["event"]);
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
                Assert.False(execution.Task.IsCompleted);
                var second = new JObject { ["id"] = "busy-2", ["command"] = "get_current_view_info" };
                writer.Dispatch(second, second.ToString(), (_, __) => throw new Exception("Busy must not enqueue UI work."));
                Assert.Contains("REVIT_BUSY", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
                execution.SetResult(new JObject { ["id"] = "modal-1", ["success"] = true, ["_operation"] = new JObject { ["execution"] = "completed", ["outcome"] = "committed" } }.ToString(Newtonsoft.Json.Formatting.None));
                var final = JObject.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Equal("committed", final["_operation"]["outcome"]);
            }
            finally { listener.Stop(); }
        }

        private static JObject Envelope() => new JObject
        {
            ["success"] = false, ["error"] = "REVIT_FAILURE_ROLLED_BACK",
            ["_failure"] = new JObject { ["schemaVersion"] = 1, ["operationId"] = "operation-1", ["incidentIds"] = new JArray("incident-1"), ["modelScopeKey"] = "model-1", ["action"] = "rolled_back", ["gate"] = "blocked", ["persistence"] = "recorded", ["complete"] = true },
            ["_operation"] = new JObject { ["operationId"] = "operation-1", ["execution"] = "completed", ["outcome"] = "partial", ["mutationApplied"] = true }
        };
    }
}
