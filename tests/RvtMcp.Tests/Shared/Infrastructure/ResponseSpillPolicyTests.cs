using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class ResponseSpillPolicyTests
    {
        [Theory]
        [InlineData("compute_room_finishes", ResponseSpillFormat.Sqlite)]
        [InlineData("export_room_data", ResponseSpillFormat.Sqlite)]
        [InlineData("get_material_takeoff", ResponseSpillFormat.Sqlite)]
        [InlineData("workflow_takeoff_report", ResponseSpillFormat.Sqlite)]
        [InlineData("batch_execute", ResponseSpillFormat.Ndjson)]
        [InlineData("export_shared_parameter_file", ResponseSpillFormat.Json)]
        [InlineData("run_baked_tool", ResponseSpillFormat.Auto)]
        [InlineData("workflow_data_roundtrip", ResponseSpillFormat.Ndjson)]
        public void Approved_group4_commands_spill_only_when_file_is_requested(
            string command,
            ResponseSpillFormat expectedFormat)
        {
            var file = ResponseSpillPolicy.Evaluate(command, "{\"output\":\"file\"}", 100);
            var inline = ResponseSpillPolicy.Evaluate(command, "{\"output\":\"inline\"}", 100);

            Assert.True(file.ShouldSpill);
            Assert.False(file.Automatic);
            Assert.Equal(expectedFormat, file.Format);
            Assert.False(inline.ShouldSpill);
        }

        [Theory]
        [InlineData("compute_room_finishes")]
        [InlineData("export_room_data")]
        [InlineData("get_material_takeoff")]
        [InlineData("workflow_takeoff_report")]
        [InlineData("batch_execute")]
        [InlineData("export_shared_parameter_file")]
        [InlineData("workflow_data_roundtrip")]
        public void Inline_mode_keeps_step1_guard_behavior_for_non_arbitrary_group4_commands(string command)
        {
            var decision = ResponseSpillPolicy.Evaluate(
                command,
                "{\"output\":\"inline\"}",
                ResponseSizeGuard.EnforcementBudgetBytes + 1);

            Assert.False(decision.ShouldSpill);
        }

        [Theory]
        [InlineData("send_code_to_revit")]
        [InlineData("run_baked_tool")]
        public void Arbitrary_code_commands_auto_spill_only_above_enforcement_budget(string command)
        {
            var large = ResponseSpillPolicy.Evaluate(
                command,
                "{}",
                ResponseSizeGuard.EnforcementBudgetBytes + 1);
            var bounded = ResponseSpillPolicy.Evaluate(
                command,
                "{}",
                ResponseSizeGuard.EnforcementBudgetBytes);

            Assert.True(large.ShouldSpill);
            Assert.True(large.Automatic);
            Assert.Equal(ResponseSpillFormat.Auto, large.Format);
            Assert.False(bounded.ShouldSpill);
        }

        [Fact]
        public void Spill_policy_extracts_only_arbitrary_user_output_for_auto_spill()
        {
            var sendData = new JObject
            {
                ["executed"] = true,
                ["result"] = new JArray(1, 2, 3)
            };
            var bakedData = new JObject
            {
                ["tool_name"] = "demo",
                ["result"] = new JObject { ["value"] = 42 }
            };

            Assert.Equal(sendData["result"], ResponseSpillPolicy.SelectPayload("send_code_to_revit", sendData));
            Assert.Equal(bakedData["result"], ResponseSpillPolicy.SelectPayload("run_baked_tool", bakedData));
            Assert.Same(sendData, ResponseSpillPolicy.SelectPayload("batch_execute", sendData));
        }
    }
}
