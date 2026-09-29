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
    public class ParametersToolShellTests
    {
        private static readonly string[] ParametersToolsInClass =
        {
            "revit_bind_shared_parameter",
            "revit_create_project_parameter",
            "revit_create_shared_parameter",
            "revit_export_shared_parameter_file",
            "revit_list_project_parameter_bindings",
            "revit_list_shared_parameters",
            "revit_remove_parameter_binding",
            "revit_set_parameter_value_by_guid"
        };

        [Fact]
        public void Every_parameters_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(ParametersTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(ParametersToolsInClass, declared);
        }

        [Fact]
        public async Task List_shared_parameters_sends_camel_case()
        {
            var sent = await Capture.Send(() => ParametersTools.ListSharedParameters(@"C:\sp.txt", "General", true, 50));

            Assert.Equal("list_shared_parameters", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\sp.txt", json.Value<string>("sharedParameterFilePath"));
            Assert.Equal("General", json.Value<string>("groupName"));
            Assert.True(json.Value<bool>("includeBindings"));
            Assert.Equal(50, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Create_shared_parameter_sends_all_flags()
        {
            var sent = await Capture.Send(() => ParametersTools.CreateSharedParameter(
                "P1", "autodesk.spec:spec.string-2.0.0", "G", "guid-1", @"C:\sp.txt", false, "d", false, false, true));

            Assert.Equal("create_shared_parameter", sent.Command);
            var json = sent.Json();
            Assert.Equal("P1", json.Value<string>("name"));
            Assert.Equal("autodesk.spec:spec.string-2.0.0", json.Value<string>("dataTypeId"));
            Assert.Equal("G", json.Value<string>("groupName"));
            Assert.Equal("guid-1", json.Value<string>("guid"));
            Assert.False(json.Value<bool>("createFileIfMissing"));
            Assert.Equal("d", json.Value<string>("description"));
            Assert.False(json.Value<bool>("visible"));
            Assert.False(json.Value<bool>("userModifiable"));
            Assert.True(json.Value<bool>("hideWhenNoValue"));
        }

        [Fact]
        public async Task Bind_shared_parameter_sends_categories_array()
        {
            var sent = await Capture.Send(() => ParametersTools.BindSharedParameter(
                "g1", new[] { "Walls", "Doors" }, "type", "pg", @"C:\sp.txt", true));

            Assert.Equal("bind_shared_parameter", sent.Command);
            var json = sent.Json();
            Assert.Equal("g1", json.Value<string>("guid"));
            Assert.Equal(2, json["categories"].Count());
            Assert.Equal("type", json.Value<string>("bindingKind"));
            Assert.Equal("pg", json.Value<string>("parameterGroupId"));
            Assert.True(json.Value<bool>("allowRebind"));
        }

        [Fact]
        public async Task Create_project_parameter_sends_spec()
        {
            var sent = await Capture.Send(() => ParametersTools.CreateProjectParameter(
                "PP", "autodesk.spec:spec.string-2.0.0", new[] { "Sheets" }, "instance", "pg"));

            Assert.Equal("create_project_parameter", sent.Command);
            var json = sent.Json();
            Assert.Equal("PP", json.Value<string>("name"));
            Assert.Single(json["categories"]);
            Assert.Equal("instance", json.Value<string>("bindingKind"));
        }

        [Fact]
        public async Task List_project_parameter_bindings_sends_filters()
        {
            var sent = await Capture.Send(() => ParametersTools.ListProjectParameterBindings(true, false, true, "IFC", "g", 25));

            Assert.Equal("list_project_parameter_bindings", sent.Command);
            var json = sent.Json();
            Assert.True(json.Value<bool>("includeCategories"));
            Assert.False(json.Value<bool>("includeShared"));
            Assert.True(json.Value<bool>("includeProject"));
            Assert.Equal("IFC", json.Value<string>("nameFilter"));
            Assert.Equal("g", json.Value<string>("guid"));
            Assert.Equal(25, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Remove_parameter_binding_sends_dry_run_default()
        {
            var sent = await Capture.Send(() => ParametersTools.RemoveParameterBinding("N", "", null, false));

            Assert.Equal("remove_parameter_binding", sent.Command);
            var json = sent.Json();
            Assert.Equal("N", json.Value<string>("name"));
            Assert.True(json.Value<bool>("dryRun"));
            Assert.False(json.Value<bool>("removeAllCategories"));
        }

        [Fact]
        public async Task Export_shared_parameter_file_sends_output_mode()
        {
            var sent = await Capture.Send(() => ParametersTools.ExportSharedParameterFile(@"C:\sp.txt", "file"));

            Assert.Equal("export_shared_parameter_file", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\sp.txt", json.Value<string>("sharedParameterFilePath"));
            Assert.Equal("file", json.Value<string>("output"));
        }

        [Fact]
        public async Task Set_parameter_value_by_guid_sends_ids_and_target()
        {
            var sent = await Capture.Send(() => ParametersTools.SetParameterValueByGuid(
                new long[] { 1, 2 }, "g9", "v", "text", "mm", "instance", false));

            Assert.Equal("set_parameter_value_by_guid", sent.Command);
            var json = sent.Json();
            Assert.Equal(2, json["elementIds"].Count());
            Assert.Equal("g9", json.Value<string>("guid"));
            Assert.Equal("v", json.Value<string>("value"));
            Assert.Equal("text", json.Value<string>("valueType"));
            Assert.Equal("mm", json.Value<string>("unit"));
            Assert.Equal("instance", json.Value<string>("target"));
            Assert.False(json.Value<bool>("allOrNothing"));
        }
    }
}
