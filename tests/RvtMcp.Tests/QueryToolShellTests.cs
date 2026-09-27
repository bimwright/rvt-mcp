using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class QueryToolShellTests
    {
        private static readonly string[] QueryToolsInClass =
        {
            "revit_ai_element_filter",
            "revit_analyze_model_statistics",
            "revit_get_assembly_members",
            "revit_get_available_family_types",
            "revit_get_current_view_info",
            "revit_get_element_details",
            "revit_get_element_parameters",
            "revit_get_element_relationships",
            "revit_get_group_members",
            "revit_get_material_quantities",
            "revit_get_selected_elements",
            "revit_get_type_parameters",
            "revit_list_assemblies",
            "revit_list_groups",
            "revit_list_project_parameters",
            "revit_list_worksets"
        };

        [Fact]
        public void Every_query_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(QueryTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(QueryToolsInClass, declared);
        }

        [Fact]
        public async Task Get_current_view_info_sends_no_payload()
        {
            var sent = await Capture.Send(() => QueryTools.GetCurrentViewInfo());

            Assert.Equal("get_current_view_info", sent.Command);
            Assert.Null(sent.Parameters);
            Assert.Null(sent.TimeoutSeconds);
        }

        [Fact]
        public async Task Gateway_failure_returns_an_error_string()
        {
            await Capture.WithSendOverride(
                (command, parameters, timeout) => throw new InvalidOperationException("Revit is running but no document is open."),
                async () =>
                {
                    var text = await QueryTools.GetCurrentViewInfo();
                    Assert.Equal("Error: Revit is running but no document is open.", text);
                });
        }

        [Fact]
        public async Task Get_selected_elements_sends_paging()
        {
            var sent = await Capture.Send(() => QueryTools.GetSelectedElements(20, 50));

            Assert.Equal("get_selected_elements", sent.Command);
            Assert.Equal(20, sent.Json().Value<int>("start_index"));
            Assert.Equal(50, sent.Json().Value<int>("max_results"));
        }

        [Fact]
        public async Task Get_available_family_types_sends_category()
        {
            var sent = await Capture.Send(() => QueryTools.GetAvailableFamilyTypes("Doors"));

            Assert.Equal("get_available_family_types", sent.Command);
            Assert.Equal("Doors", sent.Json().Value<string>("category"));
        }

        [Fact]
        public async Task Ai_element_filter_sends_filter_fields()
        {
            var sent = await Capture.Send(() => QueryTools.AiElementFilter(
                "Pipes", "Diameter", "200", "greaterthan", 25, true));

            Assert.Equal("ai_element_filter", sent.Command);
            var json = sent.Json();
            Assert.Equal("Pipes", json.Value<string>("category"));
            Assert.Equal("Diameter", json.Value<string>("parameterName"));
            Assert.Equal("200", json.Value<string>("parameterValue"));
            Assert.Equal("greaterthan", json.Value<string>("operator"));
            Assert.Equal(25, json.Value<int>("limit"));
            Assert.True(json.Value<bool>("select"));
        }

        [Fact]
        public async Task Analyze_model_statistics_omits_max_elements_by_default()
        {
            var sent = await Capture.Send(() => QueryTools.AnalyzeModelStatistics());

            Assert.Equal("analyze_model_statistics", sent.Command);
            Assert.Empty(sent.Json().Properties());
        }

        [Fact]
        public async Task Analyze_model_statistics_forwards_max_elements()
        {
            var sent = await Capture.Send(() => QueryTools.AnalyzeModelStatistics(250000));

            Assert.Equal("analyze_model_statistics", sent.Command);
            Assert.Equal(250000, sent.Json().Value<int>("maxElements"));
        }

        [Fact]
        public async Task Get_material_quantities_sends_filter_and_paging()
        {
            var sent = await Capture.Send(() => QueryTools.GetMaterialQuantities("Walls", "Concrete", 10, 40));

            Assert.Equal("get_material_quantities", sent.Command);
            var json = sent.Json();
            Assert.Equal("Walls", json.Value<string>("category"));
            Assert.Equal("Concrete", json.Value<string>("material_name_filter"));
            Assert.Equal(10, json.Value<int>("start_index"));
            Assert.Equal(40, json.Value<int>("max_results"));
        }

        [Fact]
        public async Task Get_element_details_sends_element_ids()
        {
            var sent = await Capture.Send(() => QueryTools.GetElementDetails(new long[] { 10, 20 }));

            Assert.Equal("get_element_details", sent.Command);
            Assert.Equal(new[] { 10L, 20L }, sent.Json()["elementIds"].Select(id => id.Value<long>()).ToArray());
        }

        [Fact]
        public async Task Get_element_parameters_sends_read_only_flag()
        {
            var sent = await Capture.Send(() => QueryTools.GetElementParameters(new long[] { 7 }, false));

            Assert.Equal("get_element_parameters", sent.Command);
            var json = sent.Json();
            Assert.Equal(7L, json["elementIds"][0].Value<long>());
            Assert.False(json.Value<bool>("includeReadOnly"));
        }

        [Fact]
        public async Task Get_type_parameters_omits_null_id_lists()
        {
            var sent = await Capture.Send(() => QueryTools.GetTypeParameters());

            Assert.Equal("get_type_parameters", sent.Command);
            Assert.Empty(sent.Json().Properties());
        }

        [Fact]
        public async Task List_project_parameters_sends_include_categories()
        {
            var sent = await Capture.Send(() => QueryTools.ListProjectParameters(false));

            Assert.Equal("list_project_parameters", sent.Command);
            Assert.False(sent.Json().Value<bool>("includeCategories"));
        }

        [Fact]
        public async Task Get_element_relationships_sends_dependents_flag()
        {
            var sent = await Capture.Send(() => QueryTools.GetElementRelationships(new long[] { 3 }, false));

            Assert.Equal("get_element_relationships", sent.Command);
            var json = sent.Json();
            Assert.Equal(3L, json["elementIds"][0].Value<long>());
            Assert.False(json.Value<bool>("includeDependents"));
        }

        [Fact]
        public async Task List_groups_sends_kind_and_members_flag()
        {
            var sent = await Capture.Send(() => QueryTools.ListGroups("model", true));

            Assert.Equal("list_groups", sent.Command);
            Assert.Equal("model", sent.Json().Value<string>("groupKind"));
            Assert.True(sent.Json().Value<bool>("includeMembers"));
        }

        [Fact]
        public async Task Get_group_members_sends_paging()
        {
            var sent = await Capture.Send(() => QueryTools.GetGroupMembers(15, 5, 30));

            Assert.Equal("get_group_members", sent.Command);
            var json = sent.Json();
            Assert.Equal(15L, json.Value<long>("groupId"));
            Assert.Equal(5, json.Value<int>("start_index"));
            Assert.Equal(30, json.Value<int>("max_results"));
        }

        [Fact]
        public async Task List_assemblies_sends_paging_and_member_cap()
        {
            var sent = await Capture.Send(() => QueryTools.ListAssemblies(true, 2, 10, 4));

            Assert.Equal("list_assemblies", sent.Command);
            var json = sent.Json();
            Assert.True(json.Value<bool>("includeMembers"));
            Assert.Equal(2, json.Value<int>("start_index"));
            Assert.Equal(10, json.Value<int>("max_results"));
            Assert.Equal(4, json.Value<int>("max_members_per_assembly"));
        }

        [Fact]
        public async Task Get_assembly_members_sends_paging()
        {
            var sent = await Capture.Send(() => QueryTools.GetAssemblyMembers(9, 1, 8));

            Assert.Equal("get_assembly_members", sent.Command);
            var json = sent.Json();
            Assert.Equal(9L, json.Value<long>("assemblyId"));
            Assert.Equal(1, json.Value<int>("start_index"));
            Assert.Equal(8, json.Value<int>("max_results"));
        }

        [Fact]
        public async Task List_worksets_sends_element_count_flag()
        {
            var sent = await Capture.Send(() => QueryTools.ListWorksets(true));

            Assert.Equal("list_worksets", sent.Command);
            Assert.True(sent.Json().Value<bool>("includeElementCounts"));
        }

    }
}
