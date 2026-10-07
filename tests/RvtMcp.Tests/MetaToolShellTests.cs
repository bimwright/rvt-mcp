using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class MetaToolShellTests
    {
        // revit_send_code_to_revit lives in SendCodeTools (same "meta" toolset) and is
        // covered separately; this class pins the MetaTools class surface.
        private static readonly string[] MetaToolsInClass =
        {
            "revit_analyze_usage_patterns",
            "revit_batch_execute",
            "revit_get_current_target",
            "revit_list_available_targets",
            "revit_list_recent_models",
            "revit_open_model",
            "revit_purge_unused",
            "revit_set_project_info",
            "revit_show_message",
            "revit_switch_target"
        };

        [Fact]
        public void Every_meta_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(MetaTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(MetaToolsInClass, declared);
        }

        [Fact]
        public async Task List_recent_models_sends_no_payload()
        {
            var sent = await Capture.Send(() => MetaTools.ListRecentModels());

            Assert.Equal("list_recent_models", sent.Command);
            Assert.Null(sent.Parameters);
        }

        [Fact]
        public async Task Open_model_sends_path_and_timeout_separately()
        {
            var sent = await Capture.Send(() => MetaTools.OpenModel(@"C:\models\a.rvt", false, true, "none", 120));

            Assert.Equal("open_model", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\models\a.rvt", json.Value<string>("path"));
            Assert.False(json.Value<bool>("activate"));
            Assert.True(json.Value<bool>("audit"));
            Assert.Equal("none", json.Value<string>("worksets"));
            Assert.Equal(120, sent.TimeoutSeconds);
        }

        [Fact]
        public async Task Show_message_sends_echo_fields()
        {
            var sent = await Capture.Send(() => MetaTools.ShowMessage("hi", "t", true, 64));

            Assert.Equal("show_message", sent.Command);
            var json = sent.Json();
            Assert.Equal("hi", json.Value<string>("message"));
            Assert.Equal("t", json.Value<string>("title"));
            Assert.True(json.Value<bool>("echo_message"));
            Assert.Equal(64, json.Value<int>("max_echo_chars"));
        }

        [Fact]
        public void List_available_targets_returns_discovery_payload()
        {
            var json = JObject.Parse(MetaTools.ListAvailableTargets());

            Assert.NotNull(json["discovery_dir"]);
            Assert.NotNull(json["targets"]);
            Assert.NotNull(json["count"]);
        }

        [Fact]
        public void Get_current_target_reports_pin_and_connection()
        {
            var json = JObject.Parse(MetaTools.GetCurrentTarget());

            Assert.NotNull(json["pinned_target"]);
            Assert.True(json.ContainsKey("currently_connected_year"));
            Assert.True(json.ContainsKey("currently_connected_pid"));
        }

        [Fact]
        public async Task Switch_target_rejects_r_codes()
        {
            var text = await MetaTools.SwitchTarget("R24");

            var json = JObject.Parse(text);
            Assert.False(json.Value<bool>("ok"));
            Assert.Equal("revit_list_available_targets", json.Value<string>("recommended_next_tool"));
        }

        [Fact]
        public async Task Switch_target_rejects_unknown_year()
        {
            var text = await MetaTools.SwitchTarget("1999");

            var json = JObject.Parse(text);
            Assert.False(json.Value<bool>("ok"));
            Assert.Equal("revit_list_available_targets", json.Value<string>("recommended_next_tool"));
        }

        [Fact]
        public async Task Batch_execute_sends_parsed_commands()
        {
            var sent = await Capture.Send(() => MetaTools.BatchExecute(
                "[{\"command\":\"get_current_view_info\"},{\"command\":\"get_selected_elements\",\"params\":{\"max_results\":5}}]",
                true, "file"));

            Assert.Equal("batch_execute", sent.Command);
            var json = sent.Json();
            Assert.Equal("get_current_view_info", json["commands"][0].Value<string>("command"));
            Assert.Equal(5, json["commands"][1]["params"].Value<int>("max_results"));
            Assert.True(json.Value<bool>("continueOnError"));
            Assert.Equal("file", json.Value<string>("output"));
        }

        [Fact]
        public async Task Batch_execute_rejects_over_20_commands_without_sending()
        {
            var commands = "[" + string.Join(",", Enumerable.Range(0, 21).Select(i => "{\"command\":\"noop\"}")) + "]";

            var text = await MetaTools.BatchExecute(commands);

            var json = JObject.Parse(text);
            Assert.False(json.Value<bool>("success"));
            Assert.Contains("at most 20", json.Value<string>("error"));
        }

        [Fact]
        public async Task Set_project_info_sends_snake_case_fields()
        {
            var sent = await Capture.Send(() => MetaTools.SetProjectInfo(
                name: "n", number: "42", client_name: "c", address: "a", status: "s", issue_date: "2026-01-01"));

            Assert.Equal("set_project_info", sent.Command);
            var json = sent.Json();
            Assert.Equal("n", json.Value<string>("name"));
            Assert.Equal("42", json.Value<string>("number"));
            Assert.Equal("c", json.Value<string>("client_name"));
            Assert.Equal("a", json.Value<string>("address"));
            Assert.Equal("s", json.Value<string>("status"));
            Assert.Equal("2026-01-01", json.Value<string>("issue_date"));
        }

        [Fact]
        public async Task Purge_unused_defaults_targets_to_families()
        {
            var sent = await Capture.Send(() => MetaTools.PurgeUnused(null, true, 50));

            Assert.Equal("purge_unused", sent.Command);
            var json = sent.Json();
            Assert.Equal("families", json["targets"][0].Value<string>());
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal(50, json.Value<int>("limit"));
        }

        [Fact]
        public void Analyze_usage_patterns_returns_session_and_history_shape()
        {
            var json = JObject.Parse(MetaTools.AnalyzeUsagePatterns(7));

            Assert.True(json.ContainsKey("session") || json.ContainsKey("error"));
            if (json.ContainsKey("session"))
                Assert.True(json.ContainsKey("history"));
        }
    }
}
