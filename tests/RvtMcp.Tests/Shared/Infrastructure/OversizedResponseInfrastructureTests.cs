using System.Linq;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class OversizedResponseInfrastructureTests
    {
        [Fact]
        public void Policy_catalog_returns_exact_scope_hint_for_scoped_read_tool()
        {
            var hint = ResponseSizePolicyCatalog.GetNarrowingHint("clash_detection");

            Assert.Contains("categoriesA", hint);
            Assert.Contains("categoriesB", hint);
            Assert.Contains("maxResults", hint);
        }

        [Fact]
        public void Policy_catalog_returns_new_scope_hint_for_previously_unbounded_tool()
        {
            var hint = ResponseSizePolicyCatalog.GetNarrowingHint("get_group_members");

            Assert.Contains("startIndex", hint);
            Assert.Contains("maxResults", hint);
        }

        [Theory]
        [InlineData("select_elements", "{}", true)]
        [InlineData("activate_view", "{}", true)]
        [InlineData("ai_element_filter", "{\"select\":true}", true)]
        [InlineData("ai_element_filter", "{\"select\":false}", false)]
        public void Policy_catalog_recognizes_ui_side_effects(string command, string parameters, bool expected)
        {
            Assert.Equal(expected, ResponseSizePolicyCatalog.HasUiSideEffect(command, parameters));
        }

        [Theory]
        [InlineData("send_code_to_revit", true)]
        [InlineData("run_baked_tool", true)]
        [InlineData("create_grid", false)]
        public void Mutation_policy_marks_arbitrary_code_outcomes_indeterminate(string command, bool expected)
        {
            Assert.Equal(expected, ResponseSizePolicyCatalog.IsMutationOutcomeIndeterminate(command));
        }

        [Theory]
        [InlineData("create_grid", true, true)]
        [InlineData("select_elements", false, true)]
        [InlineData("export_room_data", true, false)]
        [InlineData("export_shared_parameter_file", true, false)]
        [InlineData("workflow_model_audit", true, false)]
        public void Mutation_policy_overrides_activity_name_heuristics(
            string command,
            bool classifiedAsWrite,
            bool expected)
        {
            Assert.Equal(expected, ResponseSizePolicyCatalog.ShouldPreserveSuccessfulMutation(
                command,
                "{}",
                classifiedAsWrite));
        }

        [Fact]
        public void Paging_rejects_values_above_hard_maximum()
        {
            var request = JObject.Parse(@"{""start_index"":0,""max_results"":1001}");

            var ok = ResponsePaging.TryParse(
                request,
                "start_index",
                "max_results",
                defaultPageSize: 200,
                hardMaximum: 1000,
                out _,
                out var error);

            Assert.False(ok);
            Assert.Contains("hard maximum of 1000", error);
        }

        [Fact]
        public void Paging_slices_and_reports_continuation_metadata()
        {
            var page = ResponsePaging.Slice(Enumerable.Range(0, 10).ToArray(), startIndex: 3, maxResults: 4);

            Assert.Equal(new[] { 3, 4, 5, 6 }, page.Items);
            Assert.Equal(10, page.TotalCount);
            Assert.Equal(3, page.StartIndex);
            Assert.Equal(4, page.ReturnedCount);
            Assert.True(page.Truncated);
            Assert.Equal(7, page.NextIndex);
        }

        [Theory]
        [InlineData("dry_run", true)]
        [InlineData("dryRun", true)]
        [InlineData("rolledBack", true)]
        [InlineData("success", false)]
        [InlineData("ok", false)]
        public void Mutation_compaction_does_not_claim_dry_run_rollback_or_inner_failure_was_applied(string flag, bool value)
        {
            var compact = MutationResponseCompactor.Compact(
                new JObject { [flag] = value, ["items"] = new JArray(1, 2, 3) },
                originalByteCount: 2_000_000);

            Assert.True((bool)compact["success"]);
            Assert.False((bool)compact["mutation_applied"]);
        }

        [Fact]
        public void Mutation_compaction_preserves_success_counts_and_id_preview_under_cap()
        {
            var items = new JArray(Enumerable.Range(1, 5000).Select(i => new JObject
            {
                ["element_id"] = i,
                ["message"] = new string('x', 500)
            }));
            var original = new JObject
            {
                ["created_count"] = 5000,
                ["status"] = "committed",
                ["items"] = items
            };

            var compact = MutationResponseCompactor.Compact(original, originalByteCount: 2_700_000);
            var serialized = compact.ToString(Newtonsoft.Json.Formatting.None);

            Assert.True((bool)compact["success"]);
            Assert.True((bool)compact["response_compacted"]);
            Assert.Equal(2_700_000, (int)compact["original_byte_count"]);
            Assert.Equal(5000, (int)compact["summary"]["created_count"]);
            Assert.Equal(5000, (int)compact["collection_counts"]["items"]);
            Assert.InRange(((JArray)compact["id_preview"]["items"]).Count, 1, 20);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(serialized) < ResponseSizeGuard.DefaultThresholdBytes);
        }
    }
}
