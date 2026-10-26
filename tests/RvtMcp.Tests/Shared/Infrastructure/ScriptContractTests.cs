using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using Xunit;

namespace RvtMcp.Tests
{
    public class ScriptContractTests
    {
        [Fact]
        public void Default_input_keeps_legacy_mode_and_each_effective_option_requires_capability()
        {
            Assert.False(new ScriptInput("var data = 7; return data;").RequiresCapability);
            Assert.False(new ScriptInput("return 7;", ScriptData.FromJson("null"), Array.Empty<string>()).RequiresCapability);
            Assert.True(new ScriptInput("return 7;", ScriptData.FromJson("{}")).RequiresCapability);
            Assert.True(new ScriptInput("return 7;", modules: new[] { "helper" }).RequiresCapability);
            Assert.True(new ScriptInput("return 7;", compileOnly: true).RequiresCapability);
            Assert.True(new ScriptInput("return 7;", resultContract: ScriptModes.TaskV1).RequiresCapability);
            Assert.True(new ScriptInput("return 7;", usageContext: ScriptModes.Test).RequiresCapability);
            Assert.True(new ScriptInput("return 7;", usageContext: ScriptModes.Project).RequiresCapability);
        }

        [Fact]
        public void Invalid_contracts_contexts_and_duplicate_roots_fail_without_coercion()
        {
            Assert.Throws<ScriptContractException>(() => new ScriptInput(" "));
            Assert.Throws<ScriptContractException>(() => new ScriptInput("return 7;", resultContract: "TASK_V1"));
            Assert.Throws<ScriptContractException>(() => new ScriptInput("return 7;", usageContext: "production"));
            Assert.Throws<ScriptContractException>(() => new ScriptInput("return 7;", modules: new[] { "a", "a" }));
        }

        [Fact]
        public void Metadata_keeps_absent_stage_flags_absent_and_outside_user_data()
        {
            var data = ScriptData.FromJson("{\"logical_call_id\":\"caller-value\"}");
            var metadata = new ScriptRuntimeMetadata("return 7;") { LogicalCallId = "gateway-value", UsageContext = ScriptModes.Unknown };
            var serialized = JObject.Parse(JsonConvert.SerializeObject(metadata));
            Assert.Equal(1, serialized.Value<int>("script_protocol"));
            Assert.Null(serialized["compile_attempted"]);
            Assert.Null(serialized["invoke_started"]);
            Assert.Equal("caller-value", JObject.Parse(data.Json).Value<string>("logical_call_id"));
            Assert.Equal("gateway-value", serialized.Value<string>("logical_call_id"));
            metadata.CompileAttempted = false;
            Assert.False(JObject.Parse(JsonConvert.SerializeObject(metadata)).Value<bool>("compile_attempted"));
        }

        [Fact]
        public void Task_factories_are_sealed_immutable_and_do_not_throw_to_report_failure()
        {
            var value = new { count = 7 };
            var passed = McpTaskResult.Pass(value);
            Assert.True(passed.IsPassed);
            Assert.Same(value, passed.Value);
            Assert.Null(passed.ErrorCode);
            var failed = McpTaskResult.Fail("CHECK_FAILED", "Goal was not met.", value);
            Assert.False(failed.IsPassed);
            Assert.Same(value, failed.Details);
            Assert.Equal("CHECK_FAILED", failed.ErrorCode);
            Assert.Equal("Goal was not met.", failed.ErrorMessage);
            Assert.Null(failed.Value);
            Assert.True(typeof(McpTaskResult).IsSealed);
            Assert.All(typeof(McpTaskResult).GetProperties(), property => Assert.False(property.CanWrite));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("\t\r\n")]
        public void Task_failure_without_a_code_uses_the_specified_default(string code)
        {
            var result = McpTaskResult.Fail(code, "Goal was not met.");
            Assert.False(result.IsPassed);
            Assert.Equal("TASK_FAILED", result.ErrorCode);
            var outcome = ScriptTaskOutcome.Evaluate(ScriptModes.TaskV1, false, true, "completed", true, "unknown", result);
            Assert.False(outcome.Success);
            Assert.Equal("TASK_FAILED", outcome.ErrorCode);
        }

        [Fact]
        public void Script_not_started_is_not_a_generic_read_only_success_classifier()
        {
            var passed = McpTaskResult.Pass();
            var notStarted = ScriptTaskOutcome.Evaluate(ScriptModes.TaskV1, false, true, "completed", true, "not_started", passed);
            var unverified = ScriptTaskOutcome.Evaluate(ScriptModes.TaskV1, false, true, "completed", true, "unknown", passed);
            Assert.False(notStarted.Success);
            Assert.True(unverified.Success);
            Assert.Equal("passed", notStarted.TaskStatus);
            Assert.Equal("passed", unverified.TaskStatus);
            // Typed read-only handlers can report not_started; they must not use this script-only classifier.
        }

        [Theory]
        [InlineData("legacy", false, true, "completed", true, "unknown", "wrong", true, "not_reported", null)]
        [InlineData("legacy", false, true, "completed", true, "unknown", "fail", true, "not_reported", null)]
        [InlineData("task_v1", true, true, "completed", true, "not_started", "none", true, "not_reported", null)]
        [InlineData("task_v1", true, false, "completed", true, "not_started", "none", false, "not_reported", null)]
        [InlineData("task_v1", false, false, "completed", true, "not_started", "none", false, "not_reported", null)]
        [InlineData("task_v1", false, true, "completed", true, "unknown", "pass", true, "passed", null)]
        [InlineData("task_v1", false, true, "completed", true, "committed", "pass", true, "passed", null)]
        [InlineData("task_v1", false, true, "completed", true, "committed", "fail", false, "failed", "CHECK_FAILED")]
        [InlineData("task_v1", false, true, "completed", true, "rolled_back", "fail", false, "failed", "CHECK_FAILED")]
        [InlineData("task_v1", false, true, "completed", true, "rolled_back", "pass", false, "passed", null)]
        [InlineData("task_v1", false, true, "completed", true, "partial", "pass", false, "passed", null)]
        [InlineData("task_v1", false, true, "completed", true, "cancelled", "pass", false, "passed", null)]
        [InlineData("task_v1", false, true, "completed", true, "not_started", "pass", false, "passed", null)]
        [InlineData("task_v1", false, false, "completed", true, "failed", "pass", false, "passed", null)]
        [InlineData("task_v1", false, false, "completed", true, "failed", "fail", false, "failed", "CHECK_FAILED")]
        [InlineData("task_v1", false, true, "running", false, "unknown", "none", true, "unknown", null)]
        [InlineData("task_v1", false, false, "timed_out_running", false, "unknown", "none", false, "unknown", null)]
        [InlineData("task_v1", false, true, "completed", false, "unknown", "none", true, "unknown", null)]
        [InlineData("task_v1", false, true, "running", false, "unknown", "fail", false, "failed", "CHECK_FAILED")]
        [InlineData("task_v1", false, true, "completed", true, "unknown", "wrong", false, "not_reported", "TASK_RESULT_REQUIRED")]
        public void Task_truth_table_keeps_native_and_task_outcomes_separate(string contract, bool compileOnly,
            bool executionSucceeded, string execution, bool complete, string nativeOutcome, string resultKind,
            bool expectedSuccess, string expectedStatus, string expectedError)
        {
            object returned = resultKind == "pass" ? McpTaskResult.Pass(7) :
                resultKind == "fail" ? McpTaskResult.Fail("CHECK_FAILED", "Goal was not met.") :
                resultKind == "wrong" ? new { passed = false } : null;
            var outcome = ScriptTaskOutcome.Evaluate(contract, compileOnly, executionSucceeded, execution, complete, nativeOutcome, returned);
            Assert.Equal(expectedSuccess, outcome.Success);
            Assert.Equal(expectedStatus, outcome.TaskStatus);
            Assert.Equal(expectedError, outcome.ErrorCode);
        }

        [Fact]
        public void All_audited_pairs_have_the_expected_pure_contract_outcome()
        {
            foreach (var pair in ScriptBaselineFixture.OutcomePairs())
            {
                var legacy = ScriptTaskOutcome.Evaluate(ScriptModes.Legacy, false, true, "completed", true, "unknown", pair["legacy"]!["return_value"]);
                Assert.True(legacy.Success);
                var task = pair["task_v1"]!;
                var returned = McpTaskResult.Fail(task.Value<string>("error_code")!, task.Value<string>("message")!, task["details"]);
                var normalized = ScriptTaskOutcome.Evaluate(ScriptModes.TaskV1, false, true, "completed", true, "unknown", returned);
                Assert.False(normalized.Success);
                Assert.Equal("failed", normalized.TaskStatus);
                Assert.Equal(task.Value<string>("error_code"), normalized.ErrorCode);
            }
            // This validates the pure truth table, not P04's native/log/UI integration.
        }
    }
}
