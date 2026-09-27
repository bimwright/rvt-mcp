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
    public class OrganizationToolShellTests
    {
        private static readonly string[] OrganizationToolsInClass =
        {
            "revit_apply_view_template",
            "revit_create_view_template_from_view",
            "revit_delete_saved_selection",
            "revit_delete_view_template",
            "revit_duplicate_view_template",
            "revit_list_saved_selections",
            "revit_list_view_templates",
            "revit_load_selection",
            "revit_save_selection",
            "revit_select_elements"
        };

        [Fact]
        public void Every_organization_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(OrganizationTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(OrganizationToolsInClass, declared);
        }

        [Fact]
        public async Task List_view_templates_sends_filters()
        {
            var sent = await Capture.Send(() => OrganizationTools.ListViewTemplates("FloorPlan", 9, false, true, 50));

            Assert.Equal("list_view_templates", sent.Command);
            var json = sent.Json();
            Assert.Equal("FloorPlan", json.Value<string>("viewType"));
            Assert.Equal(9, json.Value<long>("viewId"));
            Assert.False(json.Value<bool>("includeSettings"));
            Assert.True(json.Value<bool>("includeUsage"));
            Assert.Equal(50, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Create_view_template_sends_ids()
        {
            var sent = await Capture.Send(() => OrganizationTools.CreateViewTemplateFromView(
                "T", 11, new long[] { 1 }, new long[] { 2 }, false));

            Assert.Equal("create_view_template_from_view", sent.Command);
            var json = sent.Json();
            Assert.Equal("T", json.Value<string>("templateName"));
            Assert.Equal(11, json.Value<long>("sourceViewId"));
            Assert.Single(json["controlledSettingIds"]);
            Assert.Single(json["nonControlledSettingIds"]);
            Assert.False(json.Value<bool>("failIfNameExists"));
        }

        [Fact]
        public async Task Apply_view_template_sends_mode()
        {
            var sent = await Capture.Send(() => OrganizationTools.ApplyViewTemplate(7, new long[] { 1, 2 }, "apply", true));

            Assert.Equal("apply_view_template", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("templateId"));
            Assert.Equal(2, json["viewIds"].Count());
            Assert.Equal("apply", json.Value<string>("mode"));
            Assert.True(json.Value<bool>("replaceExisting"));
        }

        [Fact]
        public async Task Duplicate_view_template_sends_name()
        {
            var sent = await Capture.Send(() => OrganizationTools.DuplicateViewTemplate(7, "T2"));

            Assert.Equal("duplicate_view_template", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("templateId"));
            Assert.Equal("T2", json.Value<string>("newName"));
        }

        [Fact]
        public async Task Delete_view_template_sends_dry_run_and_cap()
        {
            var sent = await Capture.Send(() => OrganizationTools.DeleteViewTemplate(7, true, false, 10));

            Assert.Equal("delete_view_template", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("templateId"));
            Assert.True(json.Value<bool>("dryRun"));
            Assert.False(json.Value<bool>("clearFromViews"));
            Assert.Equal(10, json.Value<int>("max_used_by_views"));
        }

        [Fact]
        public async Task Save_selection_sends_snake_case_opts()
        {
            var sent = await Capture.Send(() => OrganizationTools.SaveSelection("S1", new long[] { 1 }, true, false, true, 50));

            Assert.Equal("save_selection", sent.Command);
            var json = sent.Json();
            Assert.Equal("S1", json.Value<string>("name"));
            Assert.Single(json["elementIds"]);
            Assert.True(json.Value<bool>("replaceExisting"));
            Assert.False(json.Value<bool>("useActiveSelectionIfIdsOmitted"));
            Assert.True(json.Value<bool>("include_element_ids"));
            Assert.Equal(50, json.Value<int>("max_element_id_results"));
        }

        [Fact]
        public async Task Load_selection_sends_paging_snake_case()
        {
            var sent = await Capture.Send(() => OrganizationTools.LoadSelection("S1", null, true, 10, 50));

            Assert.Equal("load_selection", sent.Command);
            var json = sent.Json();
            Assert.Equal("S1", json.Value<string>("name"));
            Assert.True(json.Value<bool>("includeElementSummary"));
            Assert.Equal(10, json.Value<int>("start_index"));
            Assert.Equal(50, json.Value<int>("max_results"));
        }

        [Fact]
        public async Task List_saved_selections_sends_filters()
        {
            var sent = await Capture.Send(() => OrganizationTools.ListSavedSelections("f", true, true, 25));

            Assert.Equal("list_saved_selections", sent.Command);
            var json = sent.Json();
            Assert.Equal("f", json.Value<string>("nameFilter"));
            Assert.True(json.Value<bool>("includeElementIds"));
            Assert.True(json.Value<bool>("includeElementSummary"));
            Assert.Equal(25, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Delete_saved_selection_sends_selector_and_dry_run()
        {
            var sent = await Capture.Send(() => OrganizationTools.DeleteSavedSelection("S1", null, true));

            Assert.Equal("delete_saved_selection", sent.Command);
            var json = sent.Json();
            Assert.Equal("S1", json.Value<string>("name"));
            Assert.True(json.Value<bool>("dryRun"));
        }

        [Fact]
        public async Task Select_elements_sends_ids_and_zoom()
        {
            var sent = await Capture.Send(() => OrganizationTools.SelectElements(new long[] { 5 }, "", null, true));

            Assert.Equal("select_elements", sent.Command);
            var json = sent.Json();
            Assert.Single(json["elementIds"]);
            Assert.Equal("", json.Value<string>("savedSelectionName"));
            Assert.True(json.Value<bool>("zoomToSelection"));
        }
    }
}
