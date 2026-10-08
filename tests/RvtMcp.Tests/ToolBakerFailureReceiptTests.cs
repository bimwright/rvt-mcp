using System;
using System.IO;
using System.Threading.Tasks;
using Bimwright.Targeting;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.ToolBaker;
using RvtMcp.Server;
using RvtMcp.Server.Bake;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class ToolBakerFailureReceiptTests
    {
        [Theory]
        [InlineData("not_started", true, false, 0, false)]
        [InlineData("unknown", false, false, 0, false)]
        [InlineData("unknown", false, true, 0, false)]
        [InlineData("rolled_back", true, false, 1, false)]
        [InlineData("committed", true, true, 1, false)]
        [InlineData("not_started", true, false, 0, true)]
        [InlineData("unknown", false, false, 0, true)]
        public async Task Baked_run_stats_only_describe_confirmed_execution(string outcome, bool complete, bool success, int events, bool throwTargetException)
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-bake-receipt-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "bake.db");
            Directory.CreateDirectory(root);
            var factory = ToolbakerTools.RunStoreFactory;
            var send = ToolGateway.SendOverride;
            var targetContext = TargetCallContext.Begin();
            try
            {
                using var db = new BakeDb(path);
                db.Migrate();
                Assert.True(db.TryInsertRegistryRecord(new BakedToolRecord
                {
                    Name = "test_baked", Description = "Test tool", Source = "accepted", ParamsSchema = "{}", CompatMap = "{}",
                    CreatedAt = DateTimeOffset.UtcNow.ToString("o"), VersionHistoryBlob = "[]"
                }));
                ToolbakerTools.RunStoreFactory = () => new BakeDb(path);
                var receipt = new JObject { ["success"] = success, ["error"] = success ? null : "REVIT_FAILURE_ROLLED_BACK",
                    ["_operation"] = new JObject { ["operationId"] = "baked-operation", ["outcome"] = outcome,
                        ["execution"] = complete ? "completed" : "timed_out_running", ["complete"] = complete } };
                ToolGateway.SendOverride = (_, _, _) =>
                {
                    if (!throwTargetException) return Task.FromResult(receipt);
                    var notStarted = outcome == "not_started";
                    var operation = (JObject)receipt["_operation"];
                    operation["execution"] = notStarted ? "completed" : "interrupted_unknown";
                    return Task.FromException<JObject>(new RvtMcp.Server.TargetException(new TargetPayload
                    {
                        Code = notStarted ? TargetCode.TargetUnavailable : TargetCode.TargetInterrupted,
                        Sent = !notStarted,
                        Outcome = outcome
                    }, operation));
                };
                var text = await ToolbakerTools.RunBakedTool("test_baked");
                if (throwTargetException)
                {
                    Assert.StartsWith("Error: TARGET_", text);
                    Assert.NotNull(targetContext.Payload);
                    Assert.Equal("baked-operation", targetContext.Operation["operationId"]);
                    Assert.Equal(outcome, targetContext.Operation.Value<string>("outcome"));
                }
                else
                {
                    var response = JObject.Parse(text);
                    Assert.Equal("baked-operation", response["_operation"]["operationId"]);
                }
                var mcp = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_run_baked_tool" },
                    new CallToolResult { Content = new[] { new TextContentBlock { Text = text } } }, new RvtMcpConfig());
                Assert.Equal(!success || !complete, mcp.IsError == true);
                var record = Assert.Single(db.ReadRegistryRecords());
                Assert.Equal(events, JArray.Parse(record.VersionHistoryBlob).Count);
                Assert.Equal(events > 0 && success ? 1 : 0, record.UsageCount);
                if (events == 0) Assert.Null(JObject.Parse(record.CompatMap)["tested"]);
                else
                {
                    Assert.Equal(success ? "run_succeeded" : "run_failed", JArray.Parse(record.VersionHistoryBlob)[0].Value<string>("event"));
                    var tested = Assert.Single(((JObject)JObject.Parse(record.CompatMap)["tested"]).Properties());
                    Assert.Equal(success, tested.Value.Value<bool>("ok"));
                    Assert.Equal(success ? 0d : 1d, record.FailureRate);
                }
            }
            finally
            {
                ToolbakerTools.RunStoreFactory = factory;
                ToolGateway.SendOverride = send;
                TargetCallContext.End(targetContext);
                Directory.Delete(root, true);
            }
        }
    }
}
