using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    public class ChangeTrackingTests
    {
        private static JObject Summary(int count = 1)
        {
            var a = new DocumentChangeAccumulator();
            for (int i = 0; i < count; i++) a.Observe(3000000000L + i, "modified", "Walls");
            return new JObject { ["complete"] = true, ["documents"] = new JArray(a.Snapshot("Sample")) };
        }

        [Fact]
        public void Empty_capture_does_not_add_metadata() => Assert.Null(new DocumentChangeAccumulator().Snapshot("Sample"));

        [Fact]
        public void Repeated_events_collapse_and_transient_add_delete_disappears()
        {
            var a = new DocumentChangeAccumulator();
            a.Observe(1, "added", "Walls");
            a.Observe(1, "modified", "Walls");
            a.Observe(2, "modified", "Floors");
            a.Observe(2, "modified", "Floors");
            a.Observe(3, "added");
            a.Observe(3, "deleted");
            a.Observe(4, "deleted");
            var s = a.Snapshot("Sample");
            Assert.Equal(new long[] { 1 }, s["added"]["ids"].Values<long>());
            Assert.Equal(new long[] { 2 }, s["modified"]["ids"].Values<long>());
            Assert.Equal(new long[] { 4 }, s["deleted"]["ids"].Values<long>());
            Assert.Equal(1, s["by_category"].Value<int>("unknown"));
        }

        [Fact]
        public void All_transient_changes_leave_no_change_set()
        {
            var a = new DocumentChangeAccumulator();
            a.Observe(1, "added"); a.Observe(1, "deleted");
            Assert.Null(a.Snapshot("Sample"));
        }

        [Fact]
        public void Limit_preserves_exact_count_and_64_bit_ids()
        {
            var s = Summary(1000)["documents"][0];
            Assert.Equal(1000, s["modified"].Value<int>("count"));
            Assert.Equal(200, s["modified"]["ids"].Count());
            Assert.Equal(3000000000L, s["modified"]["ids"][0].Value<long>());
            Assert.True(s.Value<bool>("truncated"));
            Assert.Equal(1000, s["by_category"].Value<int>("Walls"));
        }

        [Fact]
        public void Unobservable_rollback_never_presents_observed_ids_as_final_changes()
        {
            var a = new DocumentChangeAccumulator(); a.Observe(1, "modified");
            a.MarkIncomplete("Group rollback");
            var s = a.Snapshot("Sample");
            Assert.Equal("incomplete", s.Value<string>("status"));
            Assert.Equal(JTokenType.Null, s["modified"]["count"].Type);
            Assert.Empty(s["modified"]["ids"]);
        }

        [Fact]
        public void Tracking_overflow_does_not_return_an_undercounted_complete_set()
        {
            var a = new DocumentChangeAccumulator();
            for (int i = 0; i <= DocumentChangeAccumulator.TrackingLimit; i++) a.Observe(i, "added");
            Assert.Equal("incomplete", a.Snapshot("Sample").Value<string>("status"));
        }

        [Fact]
        public void Server_allowlist_removes_paths_and_private_nested_identity()
        {
            var source = Summary();
            source["modelPath"] = "C:\\private\\sample.rvt";
            source["documents"][0]["centralPath"] = "C:\\secret\\central.rvt";
            source["documents"][0]["identity"] = new JObject { ["path"] = "private" };
            source["documents"][0]["modified"]["uniqueIds"] = new JArray("private");
            var safe = ChangeSummary.ForAgent(source).ToString();
            Assert.DoesNotContain("private", safe);
            Assert.DoesNotContain("centralPath", safe);
            Assert.Contains("3000000000", safe);
            Assert.NotNull(source["modelPath"]);
        }

        [Fact]
        public void Journal_keeps_changes_separate_from_truncated_result()
        {
            var e = JournalEntry.Create("set_element_parameter_values", "{}", true, 1,
                resultJson: new string('x', 4000), changes: Summary(200));
            Assert.Equal(2048, e.Result.Length);
            Assert.Equal(200, e.Changes["documents"][0]["modified"]["ids"].Count());
            Assert.Null(JournalEntry.Create("get_current_view_info", "{}", true, 1).Changes);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Gateway_captures_safe_metadata_before_return_or_throw(bool success)
        {
            using var capture = new ChangeCaptureContext();
            var envelope = new JObject { ["success"] = success, ["data"] = new JObject { ["count"] = 1 },
                ["error"] = "partial commit", ["changes"] = Summary() };
            envelope["changes"]["path"] = "C:\\private\\model.rvt";
            if (success) Assert.NotNull(ToolGateway.InterpretResponse(envelope)["_changes"]);
            else Assert.Throws<InvalidOperationException>(() => ToolGateway.InterpretResponse(envelope));
            Assert.NotNull(capture.Changes);
            Assert.DoesNotContain("private", capture.Changes.ToString());
        }

        [Fact]
        public void Gateway_leaves_read_response_unchanged_and_clears_prior_metadata()
        {
            using var capture = new ChangeCaptureContext();
            ChangeCaptureContext.Record(Summary());
            var data = new JObject { ["viewName"] = "Cover Sheet" };
            Assert.True(JToken.DeepEquals(data, ToolGateway.InterpretResponse(new JObject
                { ["success"] = true, ["data"] = data })));
            Assert.Null(capture.Changes);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Plugin_response_guard_bounds_metadata_for_success_and_failure(bool success)
        {
            var config = SmallBudget();
            var envelope = new JObject { ["id"] = "test", ["success"] = success,
                ["data"] = new JObject { ["ok"] = true }, ["changes"] = Summary(1000) };
            var result = ResponseEnvelopeGuard.Apply("set_element_parameter_values", "{}", envelope, config);
            Assert.True(Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) <= 1024);
            Assert.True(result["changes"].Value<bool>("truncated"));
            Assert.False(result["changes"].Value<bool>("complete"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MCP_filter_keeps_metadata_even_on_errors_and_respects_wire_budget(bool failed)
        {
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_send_code_to_revit" },
                new CallToolResult { IsError = failed, Content = new[] { new TextContentBlock { Text = failed ? "Error: failure after commit" : "{\"executed\":true}" } } },
                SmallBudget(), changes: Summary(1000));
            var s = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
            Assert.NotNull(s["_changes"]);
            Assert.Equal(failed, result.IsError == true);
            Assert.True(Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(result,
                ModelContextProtocol.McpJsonUtilities.DefaultOptions)) <= 1024);
        }

        [Fact]
        public async Task Concurrent_requests_keep_separate_change_metadata_across_awaits()
        {
            async Task<int> Run(int count)
            {
                using (var capture = new ChangeCaptureContext())
                {
                    await Task.Yield();
                    ChangeCaptureContext.Record(Summary(count));
                    await Task.Delay(5);
                    return capture.Changes["documents"][0]["modified"].Value<int>("count");
                }
            }
            Assert.Equal(new[] { 1, 2, 3 }, await Task.WhenAll(Run(1), Run(2), Run(3)));
        }

        private static RvtMcpConfig SmallBudget() => new RvtMcpConfig
        { ResponseWarnBytes = 1024, ResponseStrongWarnBytes = 1024, ResponseBudgetBytes = 1024, MaxResponseBytes = 2048 };
    }
}
