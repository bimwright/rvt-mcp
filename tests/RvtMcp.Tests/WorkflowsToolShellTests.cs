using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class WorkflowsToolShellTests
    {
        private static readonly string[] WorkflowsToolsInClass =
        {
            "revit_workflow_clash_review",
            "revit_workflow_data_roundtrip",
            "revit_workflow_model_audit",
            "revit_workflow_naming_normalization",
            "revit_workflow_room_documentation",
            "revit_workflow_sheet_set",
            "revit_workflow_takeoff_report",
            "revit_workflow_view_cleanup"
        };

        [Fact]
        public void Every_workflows_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(WorkflowsTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(WorkflowsToolsInClass, declared);
        }

        [Fact]
        public async Task Clash_review_sends_categories_and_flags()
        {
            var sent = await Capture.Send(() => WorkflowsTools.WorkflowClashReview(
                "Walls", "Floors", 7, 50, false, false, true, true, true));

            Assert.Equal("workflow_clash_review", sent.Command);
            var json = sent.Json();
            Assert.Equal("Walls", json.Value<string>("category_a"));
            Assert.Equal("Floors", json.Value<string>("category_b"));
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal(50, json.Value<int>("max_pairs"));
            Assert.False(json.Value<bool>("create_review_view"));
            Assert.True(json.Value<bool>("create_markers"));
            Assert.True(json.Value<bool>("dry_run"));
            Assert.True(json.Value<bool>("continue_on_error"));
        }

        [Fact]
        public async Task Model_audit_sends_section_flags()
        {
            var sent = await Capture.Send(() => WorkflowsTools.WorkflowModelAudit(false, true, false, true, false, 25));

            Assert.Equal("workflow_model_audit", sent.Command);
            var json = sent.Json();
            Assert.False(json.Value<bool>("include_warnings"));
            Assert.True(json.Value<bool>("include_families"));
            Assert.False(json.Value<bool>("include_views"));
            Assert.Equal(25, json.Value<int>("limit_per_section"));
        }

        [Fact]
        public async Task Room_documentation_sends_dry_run()
        {
            var sent = await Capture.Send(() => WorkflowsTools.WorkflowRoomDocumentation(
                new long[] { 1 }, "L1", false, false, false, 9, true, 10));

            Assert.Equal("workflow_room_documentation", sent.Command);
            var json = sent.Json();
            Assert.Single(json["room_ids"]);
            Assert.Equal("L1", json.Value<string>("level_name"));
            Assert.False(json.Value<bool>("create_callouts"));
            Assert.Equal(9, json.Value<long>("sheet_id"));
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal(10, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Sheet_set_sends_sheets_and_strategy()
        {
            var sent = await Capture.Send(() => WorkflowsTools.WorkflowSheetSet(
                new List<object> { new { sheet_number = "A1" } }, "prefix", true, true));

            Assert.Equal("workflow_sheet_set", sent.Command);
            var json = sent.Json();
            Assert.Single(json["sheets"]);
            Assert.Equal("prefix", json.Value<string>("renumber_strategy"));
            Assert.True(json.Value<bool>("dry_run"));
            Assert.True(json.Value<bool>("continue_on_error"));
        }

        [Fact]
        public async Task Data_roundtrip_sends_paths_and_mode()
        {
            var sent = await Capture.Send(() => WorkflowsTools.WorkflowDataRoundtrip(
                "Walls", @"C:\out.json", @"C:\in.json", "import", true, "mark", new[] { "Mark" }, "file"));

            Assert.Equal("workflow_data_roundtrip", sent.Command);
            var json = sent.Json();
            Assert.Equal("Walls", json.Value<string>("category"));
            Assert.Equal(@"C:\out.json", json.Value<string>("export_path"));
            Assert.Equal(@"C:\in.json", json.Value<string>("import_path"));
            Assert.Equal("import", json.Value<string>("mode"));
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal("mark", json.Value<string>("key_field"));
            Assert.Single(json["parameter_names"]);
            Assert.Equal("file", json.Value<string>("output"));
        }

        [Fact]
        public async Task View_cleanup_sends_sections_and_delete_flag()
        {
            var sent = await Capture.Send(() => WorkflowsTools.WorkflowViewCleanup(true, false, true, false, true, 75));

            Assert.Equal("workflow_view_cleanup", sent.Command);
            var json = sent.Json();
            Assert.True(json.Value<bool>("include_unused_views"));
            Assert.False(json.Value<bool>("include_empty_schedules"));
            Assert.True(json.Value<bool>("include_naming_outliers"));
            Assert.False(json.Value<bool>("delete_empty_views"));
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal(75, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Naming_normalization_sends_target_and_ids()
        {
            var sent = await Capture.Send(() => WorkflowsTools.WorkflowNamingNormalization(
                "sheets", "iso", "{disc}-{num}", new long[] { 1, 2 }, true, 40));

            Assert.Equal("workflow_naming_normalization", sent.Command);
            var json = sent.Json();
            Assert.Equal("sheets", json.Value<string>("target"));
            Assert.Equal("iso", json.Value<string>("profile"));
            Assert.Equal("{disc}-{num}", json.Value<string>("pattern"));
            Assert.Equal(2, json["ids"].Count());
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal(40, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Takeoff_report_sends_categories_and_output()
        {
            var sent = await Capture.Send(() => WorkflowsTools.WorkflowTakeoffReport(
                new[] { "Walls", "Floors" }, true, false, true, @"C:\out.db", 20, "file"));

            Assert.Equal("workflow_takeoff_report", sent.Command);
            var json = sent.Json();
            Assert.Equal(2, json["categories"].Count());
            Assert.True(json.Value<bool>("include_materials"));
            Assert.False(json.Value<bool>("include_quantities"));
            Assert.True(json.Value<bool>("include_cost"));
            Assert.Equal(@"C:\out.db", json.Value<string>("output_path"));
            Assert.Equal(20, json.Value<int>("limit_per_category"));
            Assert.Equal("file", json.Value<string>("output"));
        }
    }
}
