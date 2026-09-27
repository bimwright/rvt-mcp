using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class GraphicsToolShellTests
    {
        private static readonly string[] GraphicsToolsInClass =
        {
            "revit_apply_filter_to_view",
            "revit_clear_element_overrides",
            "revit_create_view_filter",
            "revit_get_view_visibility",
            "revit_list_phases",
            "revit_list_view_filters",
            "revit_override_element_graphics",
            "revit_remove_filter_from_view",
            "revit_set_category_visibility",
            "revit_set_element_phase",
            "revit_set_filter_overrides",
            "revit_set_view_phase"
        };

        [Fact]
        public void Every_graphics_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(GraphicsTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(GraphicsToolsInClass, declared);
        }

        [Fact]
        public async Task Create_view_filter_parses_rules()
        {
            var sent = await Capture.Send(() => GraphicsTools.CreateViewFilter(
                "F", new[] { "Walls" }, "[{\"field\":\"Mark\",\"op\":\"equals\",\"value\":\"X\"}]"));

            Assert.Equal("create_view_filter", sent.Command);
            var json = sent.Json();
            Assert.Equal("F", json.Value<string>("name"));
            Assert.Single(json["categories"]);
            Assert.Equal("Mark", json["rules"][0].Value<string>("field"));
        }

        [Fact]
        public async Task Apply_filter_to_view_sends_ids()
        {
            var sent = await Capture.Send(() => GraphicsTools.ApplyFilterToView(9, 7, false));

            Assert.Equal("apply_filter_to_view", sent.Command);
            var json = sent.Json();
            Assert.Equal(9, json.Value<long>("filter_id"));
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.False(json.Value<bool>("visible"));
        }

        [Fact]
        public async Task Set_filter_overrides_sends_overrides()
        {
            var sent = await Capture.Send(() => GraphicsTools.SetFilterOverrides(9, 7, "#FF0000", "#00FF00", "", 50, true, 3));

            Assert.Equal("set_filter_overrides", sent.Command);
            var json = sent.Json();
            Assert.Equal(9, json.Value<long>("filter_id"));
            Assert.Equal("#FF0000", json.Value<string>("projection_line_color"));
            Assert.Equal(50, json.Value<int>("transparency"));
            Assert.True(json.Value<bool>("halftone"));
            Assert.Equal(3, json.Value<int>("projection_line_weight"));
        }

        [Fact]
        public async Task List_view_filters_sends_scope()
        {
            var sent = await Capture.Send(() => GraphicsTools.ListViewFilters(7, true));

            Assert.Equal("list_view_filters", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.True(json.Value<bool>("include_usage"));
        }

        [Fact]
        public async Task Remove_filter_from_view_sends_delete_flag()
        {
            var sent = await Capture.Send(() => GraphicsTools.RemoveFilterFromView(9, 7, true));

            Assert.Equal("remove_filter_from_view", sent.Command);
            var json = sent.Json();
            Assert.Equal(9, json.Value<long>("filter_id"));
            Assert.True(json.Value<bool>("delete_definition_if_unused"));
        }

        [Fact]
        public async Task Override_element_graphics_sends_ids()
        {
            var sent = await Capture.Send(() => GraphicsTools.OverrideElementGraphics(new long[] { 1 }, 7, "#FF0000", "", "", 30, null, null));

            Assert.Equal("override_element_graphics", sent.Command);
            var json = sent.Json();
            Assert.Single(json["element_ids"]);
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal(30, json.Value<int>("transparency"));
        }

        [Fact]
        public async Task Clear_element_overrides_sends_ids()
        {
            var sent = await Capture.Send(() => GraphicsTools.ClearElementOverrides(new long[] { 1 }, 7));

            Assert.Equal("clear_element_overrides", sent.Command);
            var json = sent.Json();
            Assert.Single(json["element_ids"]);
            Assert.Equal(7, json.Value<long>("view_id"));
        }

        [Fact]
        public async Task Get_view_visibility_sends_flags()
        {
            var sent = await Capture.Send(() => GraphicsTools.GetViewVisibility(7, true));

            Assert.Equal("get_view_visibility", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.True(json.Value<bool>("include_category_list"));
        }

        [Fact]
        public async Task Set_category_visibility_sends_categories()
        {
            var sent = await Capture.Send(() => GraphicsTools.SetCategoryVisibility(new[] { "Walls" }, true, 7));

            Assert.Equal("set_category_visibility", sent.Command);
            var json = sent.Json();
            Assert.Single(json["categories"]);
            Assert.True(json.Value<bool>("hidden"));
            Assert.Equal(7, json.Value<long>("view_id"));
        }

        [Fact]
        public async Task List_phases_sends_empty_object()
        {
            var sent = await Capture.Send(() => GraphicsTools.ListPhases());

            Assert.Equal("list_phases", sent.Command);
            Assert.Empty(sent.Json());
        }

        [Fact]
        public async Task Set_view_phase_sends_phase_fields()
        {
            var sent = await Capture.Send(() => GraphicsTools.SetViewPhase(7, 12589, "", null, ""));

            Assert.Equal("set_view_phase", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal(12589, json.Value<long>("phase_id"));
        }

        [Fact]
        public async Task Set_element_phase_sends_created_and_demolished()
        {
            var sent = await Capture.Send(() => GraphicsTools.SetElementPhase(new long[] { 1 }, 12589, "", 86961, ""));

            Assert.Equal("set_element_phase", sent.Command);
            var json = sent.Json();
            Assert.Single(json["element_ids"]);
            Assert.Equal(12589, json.Value<long>("phase_created_id"));
            Assert.Equal(86961, json.Value<long>("phase_demolished_id"));
        }
    }
}
