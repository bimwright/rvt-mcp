using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class RuntimeGuardRegressionTests
    {
        [Theory]
        [InlineData("dry_run", true)]
        [InlineData("dryRun", true)]
        [InlineData("rolledBack", true)]
        [InlineData("success", false)]
        [InlineData("ok", false)]
        public void Repeated_compaction_preserves_outcome_counts_and_original_size(string flag, bool value)
        {
            var first = MutationResponseCompactor.Compact(new JObject
            {
                [flag] = value, ["items"] = new JArray(1, 2, 3), ["output_path"] = "C:\\test\\report.json"
            }, 30_000);
            var again = MutationResponseCompactor.Compact(first, 8_000);
            Assert.False(again.Value<bool>("mutation_applied"));
            Assert.True(JToken.DeepEquals(first, again));
            Assert.NotSame(first, again);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        [InlineData(null)]
        public void Explicit_mutation_outcome_is_not_reinferred(bool? mutation)
        {
            var data = new JObject { ["mutation_applied"] = mutation.HasValue ? new JValue(mutation.Value) : JValue.CreateNull() };
            var compact = MutationResponseCompactor.Compact(data, 30_000);
            Assert.True(JToken.DeepEquals(data["mutation_applied"], compact["mutation_applied"]));
        }

        [Fact]
        public void Plugin_then_MCP_guard_keeps_unicode_template_dry_run_unapplied()
        {
            var config = new RvtMcpConfig { ResponseWarnBytes = 1024, ResponseStrongWarnBytes = 2048,
                ResponseBudgetBytes = 2048, MaxResponseBytes = 8192 };
            var data = new JObject
            {
                ["dryRun"] = true, ["wouldDelete"] = false, ["deleted"] = false,
                ["templateId"] = 123, ["templateName"] = new string('界', 250), ["success"] = true,
                ["usedByViewCount"] = 100, ["usedByViews"] = new JArray(Enumerable.Range(0, 100).Select(i =>
                    new JObject { ["viewId"] = 1000 + i, ["name"] = new string('界', 250), ["viewType"] = "FloorPlan" })),
                ["usedByViewsTruncated"] = false, ["clearFromViews"] = false, ["deletedElementCount"] = 0,
                ["deletedElementIds"] = new JArray(), ["deletedElementIdsTruncated"] = false
            };
            var arguments = new JObject { ["dryRun"] = true };
            var plugin = PluginGuard("delete_view_template", arguments, data, config);
            Assert.False(plugin["data"].Value<bool>("mutation_applied"));
            var input = Text(plugin["data"]);
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(input)) > config.ResponseBudgetBytesOrDefault);
            var server = ServerGuard("delete_view_template", arguments, input, config);
            Assert.NotEqual(true, server.IsError);
            Assert.False(ResultData(server).Value<bool>("mutation_applied"));
            Assert.Equal(plugin["data"].Value<int>("original_byte_count"), ResultData(server).Value<int>("original_byte_count"));
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(server)) <= config.ResponseBudgetBytesOrDefault);
        }

        [Theory]
        [InlineData("inline", "C:\\test\\report.json", true)]
        [InlineData("inline", "C:\\test\\report.csv", true)]
        [InlineData("file", "", true)]
        [InlineData("inline", "", false)]
        [InlineData("inline", " ", false)]
        public void Takeoff_policy_recognizes_both_file_outputs(string output, string path, bool expected)
        {
            var arguments = new JObject { ["output"] = output, ["output_path"] = path };
            Assert.Equal(expected, ResponseSizePolicyCatalog.ShouldPreserveSuccessfulMutation(
                "workflow_takeoff_report", arguments.ToString(), true));
        }

        [Theory]
        [InlineData("C:\\test\\report.json")]
        [InlineData("C:\\test\\report.csv")]
        public void Completed_takeoff_export_survives_plugin_and_server_guards(string path)
        {
            var config = Limits();
            var arguments = new JObject { ["output"] = "inline", ["output_path"] = path };
            var data = TakeoffData("succeeded", path);
            var plugin = PluginGuard("workflow_takeoff_report", arguments, data, config);
            Assert.True(plugin.Value<bool>("success"));
            Assert.True(plugin["data"].Value<bool>("mutation_applied"));
            Assert.Equal(path, plugin["data"]["summary"].Value<string>("output_path"));
            var server = ServerGuard("workflow_takeoff_report", arguments, Text(plugin["data"]), config);
            Assert.NotEqual(true, server.IsError);
            var result = ResultData(server);
            Assert.True(result.Value<bool>("mutation_applied"));
            Assert.Equal(path, result["summary"].Value<string>("output_path"));
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(server)) <= config.ResponseBudgetBytesOrDefault);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(null)]
        public void Two_stage_guard_preserves_explicit_unapplied_or_unknown_outcomes(bool? mutation)
        {
            var config = Limits();
            var data = new JObject
            {
                ["mutation_applied"] = mutation.HasValue ? new JValue(mutation.Value) : JValue.CreateNull(),
                ["detail"] = new string('x', 16000)
            };
            var plugin = PluginGuard("set_element_parameter_values", new JObject(), data, config);
            var server = ServerGuard("set_element_parameter_values", new JObject(), Text(plugin["data"]), config);
            Assert.NotEqual(true, server.IsError);
            Assert.True(JToken.DeepEquals(data["mutation_applied"], ResultData(server)["mutation_applied"]));
        }

        [Fact]
        public void Completed_takeoff_export_keeps_destination_in_the_MCP_minimal_fallback()
        {
            var path = "C:\\test\\" + new string('界', 130) + ".json";
            var arguments = new JObject { ["output_path"] = path };
            var data = TakeoffData("succeeded", path);
            data["title"] = new string('界', 250);
            var config = new RvtMcpConfig { ResponseWarnBytes = 1024, ResponseStrongWarnBytes = 2048,
                ResponseBudgetBytes = 2048, MaxResponseBytes = 8192 };
            // A plugin summary can fit its own cap but exceed the MCP cap after escaping.
            var plugin = PluginGuard("workflow_takeoff_report", arguments, data, Limits());
            Assert.True(plugin.Value<bool>("success"));
            var input = Text(plugin["data"]);
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(input)) > config.ResponseBudgetBytesOrDefault);
            var server = ServerGuard("workflow_takeoff_report", arguments, input, config);
            Assert.NotEqual(true, server.IsError);
            Assert.True(ResultData(server).Value<bool>("mutation_applied"));
            var compact = ResultData(server);
            Assert.Null(compact["summary"]);
            Assert.Equal(path, compact.Value<string>("path") ?? compact.Value<string>("output_path"));
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(server)) <= config.ResponseBudgetBytesOrDefault);
        }

        [Fact]
        public void Takeoff_dry_run_with_a_destination_is_not_reported_as_a_file_write()
        {
            var arguments = new JObject { ["dry_run"] = true, ["output_path"] = "C:\\test\\report.json" };
            var data = TakeoffData("dry_run", null);
            data["dry_run"] = true;
            var plugin = PluginGuard("workflow_takeoff_report", arguments, data, Limits());
            Assert.True(plugin.Value<bool>("success"));
            Assert.False(plugin["data"].Value<bool>("mutation_applied"));
            var server = ServerGuard("workflow_takeoff_report", arguments, Text(plugin["data"]), Limits());
            Assert.NotEqual(true, server.IsError);
            Assert.False(ResultData(server).Value<bool>("mutation_applied"));
        }

        [Theory]
        [InlineData(20)]
        [InlineData(16000)]
        public void Failed_takeoff_export_is_not_promoted_to_a_completed_write(int detailChars)
        {
            var arguments = new JObject { ["output"] = "inline", ["output_path"] = "C:\\test\\report.json" };
            var data = TakeoffData("failed", "C:\\test\\report.json", detailChars);
            var plugin = PluginGuard("workflow_takeoff_report", arguments, data, Limits());
            Assert.False(plugin.Value<bool>("success"));
            Assert.Null(plugin["data"]?["mutation_applied"]);
            var server = ServerGuard("workflow_takeoff_report", arguments, Text(data), Limits());
            Assert.True(server.IsError);
            Assert.NotEqual(true, ResultData(server).Value<bool?>("mutation_applied"));
        }

        [Fact]
        public void Takeoff_without_a_file_destination_remains_an_oversized_read_error()
        {
            var arguments = new JObject { ["output"] = "inline" };
            var plugin = PluginGuard("workflow_takeoff_report", arguments, TakeoffData("succeeded", null), Limits());
            Assert.False(plugin.Value<bool>("success"));
            Assert.Contains("RESPONSE_TOO_LARGE", plugin.Value<string>("error"));
        }

        private static JObject TakeoffData(string status, string path, int detailChars = 16000)
            => new JObject
            {
                ["workflow"] = "workflow_takeoff_report", ["status"] = status, ["dry_run"] = false,
                ["output_path"] = path, ["quantities"] = new string('x', detailChars),
                ["steps"] = new JArray(new JObject { ["tool"] = "file_export", ["status"] = status,
                    ["error"] = status == "failed" ? "Permission denied." : null })
            };

        private static JObject PluginGuard(string command, JObject arguments, JObject data, RvtMcpConfig config)
            => ResponseEnvelopeGuard.Apply(command, arguments.ToString(), new JObject
            {
                ["id"] = "regression", ["success"] = true, ["data"] = data
            }, config);

        private static CallToolResult ServerGuard(string command, JObject arguments, CallToolResult result, RvtMcpConfig config)
            => RuntimeToolFilter.Apply(new CallToolRequestParams
            {
                Name = "revit_" + command,
                Arguments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToString())
            }, result, config);

        private static CallToolResult Text(JToken data) => new CallToolResult
        {
            Content = new[] { new TextContentBlock { Text = data.ToString(Formatting.Indented) } }
        };

        private static JObject ResultData(CallToolResult result)
            => JObject.Parse(((TextContentBlock)result.Content[0]).Text);

        private static RvtMcpConfig Limits() => new RvtMcpConfig
        {
            ResponseWarnBytes = 2048, ResponseStrongWarnBytes = 4096, ResponseBudgetBytes = 8192, MaxResponseBytes = 32768
        };
    }
}
