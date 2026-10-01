using System;
using System.IO;
using System.Linq;
using System.Text;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class ChangeHistoryRuntimeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-history-runtime-" + Guid.NewGuid().ToString("N"));
        private readonly ChangeHistoryStore _original = ToolGateway.History;
        private readonly RvtMcpConfig _config = ServerState.Config;
        public ChangeHistoryRuntimeTests() { ToolGateway.History = new ChangeHistoryStore(_root); }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async System.Threading.Tasks.Task Active_history_query_resolves_identity_without_creating_storage(bool readOnly)
        {
            var originalSend = ToolGateway.SendOverride;
            var key = new string('a', 64);
            try
            {
                ServerState.Config = new RvtMcpConfig { ReadOnly = readOnly };
                ToolGateway.SendOverride = (command, args, timeout) =>
                {
                    Assert.Equal("get_change_records", command);
                    return System.Threading.Tasks.Task.FromResult(new JObject { ["modelKey"] = key });
                };
                var result = JObject.Parse(await ChangeHistoryTools.GetChangeRecords());
                Assert.Equal(key, result.Value<string>("modelKey")); Assert.Empty(result["calls"]);
                Assert.False(Directory.Exists(_root));
                ToolGateway.SendOverride = (command, args, timeout) => throw new Exception("Explicit key must work offline.");
                Assert.Empty(JObject.Parse(await ChangeHistoryTools.GetChangeRecords(key))["calls"]);
                Assert.False(Directory.Exists(_root));
            }
            finally { ToolGateway.SendOverride = originalSend; }
        }

        [Fact]
        public async System.Threading.Tasks.Task Unsaved_active_history_query_reports_missing_identity_without_creating_storage()
        {
            var originalSend = ToolGateway.SendOverride;
            try
            {
                ToolGateway.SendOverride = (command, args, timeout) => System.Threading.Tasks.Task.FromResult(new JObject());
                Assert.Contains("no stable history identity", await ChangeHistoryTools.GetChangeRecords());
                Assert.False(Directory.Exists(_root));
            }
            finally { ToolGateway.SendOverride = originalSend; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Storage_failure_preserves_mutation_outcome_and_pending_data_without_paths(bool failed)
        {
            var payload = ChangeHistoryTests.Capture(); var id = payload.Value<string>("callId");
            ToolGateway.History.Transfer.Write(id, payload);
            File.WriteAllText(Path.Combine(_root, "projects"), "not a directory");
            using var capture = new ChangeCaptureContext();
            var response = new JObject { ["success"] = !failed, ["data"] = new JObject { ["applied"] = true }, ["error"] = "failure after commit",
                ["history_transfer"] = new JObject { ["id"] = id } };
            ToolGateway.CaptureHistory(response, new ChangeHistoryRequest { Id = id, Session = payload.Value<string>("session") });
            Assert.Equal("storage_failed", capture.History.Value<string>("status"));
            if (failed) Assert.Throws<InvalidOperationException>(() => ToolGateway.InterpretResponse(response));
            else Assert.True(ToolGateway.InterpretResponse(response).Value<bool>("applied"));
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_send_code_to_revit" },
                new CallToolResult { IsError = failed, Content = new[] { new TextContentBlock { Text = failed ? "Error: failure after commit" : "{\"applied\":true}" } } },
                new RvtMcpConfig(), history: capture.History);
            Assert.Equal(failed, result.IsError == true);
            var text = ((TextContentBlock)result.Content[0]).Text;
            Assert.DoesNotContain(_root, text); Assert.DoesNotContain("private-client", text); Assert.Contains("storage_failed", text);
            Assert.Single(ToolGateway.History.Transfer.PendingIds());
        }

        [Fact]
        public async System.Threading.Tasks.Task Disabled_history_performs_no_transfer_io_and_keeps_existing_history_readable()
        {
            using var capture = new ChangeCaptureContext();
            ToolGateway.CaptureHistory(new JObject { ["history_transfer"] = new JObject { ["id"] = Guid.NewGuid().ToString("N") } }, null);
            Assert.Null(capture.History); Assert.False(Directory.Exists(_root));
            var payload = ChangeHistoryTests.Capture(); ToolGateway.History.Ingest(payload);
            ServerState.Config = new RvtMcpConfig { EnableChangeHistory = false };
            var key = payload["activeModel"].Value<string>("key");
            Assert.Single(JObject.Parse(await ChangeHistoryTools.GetChangeRecords(key))["calls"]);
            Assert.Contains("disabled", ChangeHistoryTools.RecordChange(key, "[]", "request", "goal"));
        }

        [Fact]
        public void Private_transfer_identifier_is_never_interpreted_as_agent_data()
        {
            var id = Guid.NewGuid().ToString("N");
            var response = new JObject { ["success"] = true, ["data"] = new JObject { ["ok"] = true },
                ["history_transfer"] = new JObject { ["id"] = id, ["path"] = @"C:\private\model.rvt" } };
            var data = ToolGateway.InterpretResponse(response).ToString();
            Assert.DoesNotContain(id, data); Assert.DoesNotContain("private", data); Assert.DoesNotContain("history_transfer", data);
        }

        [Fact]
        public void Receipt_and_change_summary_obey_small_wire_budget()
        {
            var history = new JObject { ["status"] = "recorded", ["callId"] = Guid.NewGuid().ToString("N"), ["modelKey"] = new string('a', 64),
                ["models"] = new JArray(Enumerable.Range(0, 8).Select(i => new JObject { ["title"] = new string('x', 4000), ["modelKey"] = new string('a', 64) })) };
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_set_element_parameter_values" },
                new CallToolResult { Content = new[] { new TextContentBlock { Text = "{\"ok\":true}" } } },
                new RvtMcpConfig { ResponseWarnBytes = 1024, ResponseStrongWarnBytes = 1024, ResponseBudgetBytes = 1024, MaxResponseBytes = 2048 }, history: history);
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= 1024);
            Assert.Equal(history.Value<string>("callId"), JObject.Parse(((TextContentBlock)result.Content[0]).Text)["_history"].Value<string>("callId"));
        }

        public void Dispose()
        {
            ToolGateway.History = _original; ServerState.Config = _config;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
