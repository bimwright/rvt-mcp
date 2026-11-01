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
    public class ReportCaptureReceiptTests
    {
        private static JObject Receipt() => new JObject { ["call_id"] = Guid.NewGuid().ToString("N"), ["client_session"] = new string('a', 32), ["status"] = "captured" };
        [Theory] [InlineData(1024)] [InlineData(4096)] [InlineData(65536)]
        public void Mcp_compaction_preserves_receipts_or_explicitly_labels_partial_receipt_coverage(int cap)
        {
            var receipts = new JArray(Enumerable.Range(0, 5).Select(i => Receipt()));
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_synthetic" },
                new CallToolResult { Content = new[] { new TextContentBlock { Text = new JObject { ["blob"] = new string('x', 100000) }.ToString() } } },
                new RvtMcpConfig { MaxResponseBytes = cap, EnableResponseGuard = true, ResponseBudgetBytes = cap }, reportCaptures: receipts);
            var data = JObject.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
            Assert.True(data["_report_capture"] != null || data.Value<bool?>("_report_captures_truncated") == true);
            if (data.Value<bool?>("_report_captures_truncated") != true) Assert.Equal(5, data["_report_captures"].Count());
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= cap);
        }
        [Fact] public void Gateway_uses_only_the_native_envelope_receipt_and_carries_it_through_wrappers()
        {
            var fake = Receipt(); var actual = Receipt();
            var data = ToolGateway.InterpretResponse(new JObject { ["success"] = true,
                ["data"] = new JObject { ["_report_capture"] = fake }, ["_report_capture"] = actual });
            Assert.True(JToken.DeepEquals(actual, data["_report_capture"]));
            var wrapped = new JObject(); ToolGateway.CarryOnce(data, wrapped);
            Assert.True(JToken.DeepEquals(actual, wrapped["_report_capture"]));
            var untrusted = ToolGateway.InterpretResponse(new JObject { ["success"] = true, ["data"] = new JObject { ["_report_capture"] = fake } });
            Assert.Null(untrusted["_report_capture"]);
        }
        [Fact] public void Failed_native_call_keeps_its_capture_receipt()
        {
            var receipt = Receipt();
            var data = ToolGateway.InterpretResponse(new JObject { ["success"] = false, ["error"] = "Synthetic partial failure", ["_report_capture"] = receipt });
            Assert.False(data.Value<bool>("success")); Assert.True(JToken.DeepEquals(receipt, data["_report_capture"]));
        }
        [Fact] public void All_native_calls_of_a_wrapper_are_retained_in_the_async_context()
        {
            using var context = new ChangeCaptureContext();
            var a = Receipt(); var b = Receipt();
            ToolGateway.InterpretResponse(new JObject { ["success"] = true, ["data"] = new JObject(), ["_report_capture"] = a });
            ToolGateway.InterpretResponse(new JObject { ["success"] = true, ["data"] = new JObject(), ["_report_capture"] = b });
            Assert.Equal(2, context.ReportCaptures.Count);
        }
    }
}
