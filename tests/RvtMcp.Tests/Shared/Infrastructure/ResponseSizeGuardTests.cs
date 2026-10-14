using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class ResponseSizeGuardTests
    {
        [Fact]
        public void Check_under_warning_threshold_returns_null()
        {
            var payload = new string('x', ResponseSizeGuard.DefaultThresholdBytes - 1);
            var warning = ResponseSizeGuard.CheckResponse("some_command", payload, topLevelKeyCount: 3);
            Assert.Null(warning);
        }

        [Fact]
        public void Evaluate_exactly_at_64_kib_returns_agent_visible_warning()
        {
            var payload = new string('x', 64 * 1024);
            var decision = ResponseSizeGuard.Evaluate("ai_element_filter", payload, topLevelKeyCount: 2);

            Assert.False(decision.Reject);
            Assert.Equal("warning", decision.WarningLevel);
            Assert.Contains("ai_element_filter", decision.Warning);
            Assert.Contains("65536", decision.AgentWarning);
        }

        [Fact]
        public void Evaluate_above_256_kib_returns_strong_warning_with_tool_hint()
        {
            var payload = new string('x', 256 * 1024 + 1);
            var decision = ResponseSizeGuard.Evaluate(
                "clash_detection",
                payload,
                topLevelKeyCount: 8,
                narrowingHint: "Use categoriesA, categoriesB, and a smaller maxResults.");

            Assert.False(decision.Reject);
            Assert.Equal("strong_warning", decision.WarningLevel);
            Assert.Contains("strong warning", decision.AgentWarning);
            Assert.Contains("maxResults", decision.AgentWarning);
        }

        [Fact]
        public void Check_threshold_is_configurable()
        {
            var payload = new string('x', 50);
            var warning = ResponseSizeGuard.CheckResponse("some_command", payload, topLevelKeyCount: 1, thresholdBytes: 40);
            Assert.NotNull(warning);
        }

        [Fact]
        public void Evaluate_above_max_rejects_with_command_specific_hint()
        {
            var payload = new string('x', ResponseSizeGuard.MaxResponseBytes + 1);
            var decision = ResponseSizeGuard.Evaluate(
                "clash_detection",
                payload,
                topLevelKeyCount: 8,
                narrowingHint: "Use categoriesA, categoriesB, and maxResults (hard maximum 500).");

            Assert.True(decision.Reject);
            Assert.Contains("clash_detection", decision.RejectError);
            Assert.Contains("categoriesA", decision.RejectError);
            Assert.Contains("maxResults", decision.RejectError);
            Assert.Contains("Do not retry", decision.RejectError);
            Assert.DoesNotContain("filters, ids, max_results, pagination", decision.RejectError);
        }

        [Fact]
        public void Enforcement_budget_leaves_headroom_below_delivered_ceiling()
        {
            // The server pretty-prints (Formatting.Indented) before the payload reaches
            // the agent, inflating it past the compact bytes measured here. The plugin-side
            // budget must sit below the 1 MiB delivered ceiling to absorb that expansion.
            Assert.True(ResponseSizeGuard.EnforcementBudgetBytes < ResponseSizeGuard.MaxResponseBytes);
        }

        [Fact]
        public void Evaluate_above_budget_but_below_ceiling_rejects_for_pretty_print_headroom()
        {
            var payload = new string('x', ResponseSizeGuard.EnforcementBudgetBytes + 1);
            var decision = ResponseSizeGuard.Evaluate("list_rooms", payload, topLevelKeyCount: 1);

            Assert.True(decision.Reject);
            Assert.Contains("list_rooms", decision.RejectError);
        }

        [Fact]
        public void Evaluate_just_under_budget_warns_strongly_without_rejecting()
        {
            var payload = new string('x', ResponseSizeGuard.EnforcementBudgetBytes - 1);
            var decision = ResponseSizeGuard.Evaluate("list_rooms", payload, topLevelKeyCount: 1);

            Assert.False(decision.Reject);
            Assert.Equal("strong_warning", decision.WarningLevel);
        }

        [Fact]
        public void Evaluate_counts_utf8_bytes_not_utf16_characters()
        {
            var decision = ResponseSizeGuard.Evaluate(
                "some_command",
                new string('đ', 40 * 1024),
                topLevelKeyCount: 1);

            Assert.Equal(80 * 1024, decision.ByteCount);
            Assert.Equal("warning", decision.WarningLevel);
        }
    }
}
