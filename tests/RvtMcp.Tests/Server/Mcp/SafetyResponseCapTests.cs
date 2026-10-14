using System;
using System.Linq;
using System.Text;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class SafetyResponseCapTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Home_target_stamp_preserves_nullable_strings_before_serialization(bool compact)
        {
            string noDocument = null;
            var source = new JObject
            {
                ["_target"] = new JObject
                {
                    ["target_id"] = "revit-2024-87724", ["pid"] = 87724, ["host_year"] = 2024,
                    ["document_id"] = noDocument, ["document_title"] = noDocument
                }
            };
            // A JSON round-trip changes this to Null and would conceal the host-side defect.
            Assert.Equal(JTokenType.String, source["_target"]["document_id"].Type);
            var result = new JObject();
            SafetyResponseMetadata.Attach(result, source, compact);
            Assert.Equal(87724, result["_target"].Value<int>("pid"));
            Assert.Null(result["_target"].Value<string>("document_id"));
            if (!compact) Assert.Null(result["_target"].Value<string>("document_title"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Receipt_compaction_accepts_nullable_scope_and_dialog_text(bool compact)
        {
            string absent = null;
            var receipt = new JObject
            {
                ["operationId"] = "0123456789abcdef0123456789abcdef", ["execution"] = "completed",
                ["outcome"] = "not_started", ["mutationApplied"] = false, ["modelScopeKey"] = absent,
                ["dialog"] = new JObject { ["text"] = absent }, ["padding"] = new string('x', 5000)
            };
            var result = SafetyResponseMetadata.Bound(receipt, 1024, compact);
            Assert.Equal("not_started", result.Value<string>("outcome"));
            Assert.False(result.Value<bool>("mutationApplied"));
            Assert.Null(result.Value<string>("modelScopeKey"));
        }

        [Theory]
        [InlineData(1024, 1024)]
        [InlineData(1536, 2048)]
        [InlineData(4096, 5120)]
        public void Safety_receipts_fit_the_delivered_MCP_budget_without_losing_outcome(int budget, int cap)
        {
            const string operationId = "0123456789abcdef0123456789abcdef";
            var config = new RvtMcpConfig { ResponseWarnBytes = 1024, ResponseStrongWarnBytes = 1024,
                ResponseBudgetBytes = budget, MaxResponseBytes = cap };
            config.ValidateResponseLimits();
            var envelope = new JObject
            {
                ["success"] = false, ["error"] = "REVIT_FAILURE_ROLLED_BACK", ["details"] = new string('x', 200000),
                ["_target"] = new JObject { ["target_id"] = "revit-2027-474747", ["pid"] = 474747, ["host_year"] = 2027,
                    ["document_id"] = "123456789abcdef0123456789abcdef0", ["window_title"] = new string('x', 1024) },
                ["_failure"] = new JObject
                {
                    ["schemaVersion"] = 1, ["operationId"] = operationId, ["hostInstanceId"] = "474747-638955648000000000",
                    ["modelScopeKey"] = new string('a', 64), ["action"] = "rolled_back", ["mutationApplied"] = false,
                    ["gate"] = "blocked", ["persistence"] = "recorded", ["complete"] = true,
                    ["messageCount"] = 100, ["observationCoverage"] = "supported_handlers_only",
                    ["incidentIds"] = new JArray(Enumerable.Range(0, 100).Select(i => i.ToString("x32")))
                },
                ["_operation"] = new JObject
                {
                    ["operationId"] = operationId, ["hostInstanceId"] = "474747-638955648000000000",
                    ["execution"] = "completed", ["outcome"] = "rolled_back", ["mutationApplied"] = false,
                    ["activeModelBefore"] = new string('a', 64), ["activeModelAfter"] = new string('a', 64),
                    ["complete"] = true, ["requiresReview"] = false, ["observationCoverage"] = "supported_handlers_only"
                }
            };
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_create_wall" },
                new CallToolResult { Content = new[] { new TextContentBlock { Text = envelope.ToString() } } }, config);
            var payload = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
            Assert.True(result.IsError == true);
            Assert.Equal(474747, payload["_target"].Value<int>("pid"));
            Assert.Equal("123456789abcdef0123456789abcdef0", payload["_target"].Value<string>("document_id"));
            Assert.Equal(operationId, payload["_operation"]["operationId"]);
            Assert.Equal("rolled_back", payload["_operation"]["outcome"]);
            Assert.False(payload["_operation"].Value<bool>("mutationApplied"));
            Assert.Equal("blocked", payload["_failure"]["gate"]);
            Assert.Equal("recorded", payload["_failure"]["persistence"]);
            Assert.True(payload["_failure"].Value<bool>("complete"));
            Assert.True(payload["_failure"].Value<bool>("incidentIdsOmitted"));
            var bytes = Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result));
            // Include a realistic JSON-RPC envelope, not just the receipt's own JSON size.
            var delivered = "{\"jsonrpc\":\"2.0\",\"id\":\"review-operation-receipt\",\"result\":" + RuntimeToolFilter.Serialize(result) + "}";
            Assert.True(bytes <= budget, $"Delivered result was {bytes} bytes (budget={budget}).");
            Assert.True(Encoding.UTF8.GetByteCount(delivered) <= cap, $"JSON-RPC response exceeded the {cap}-byte transport cap.");
        }
    }
}
