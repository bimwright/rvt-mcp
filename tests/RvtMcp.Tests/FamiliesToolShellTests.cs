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
    public class FamiliesToolShellTests
    {
        private static readonly string[] FamiliesToolsInClass =
        {
            "revit_audit_families",
            "revit_duplicate_family_type",
            "revit_export_family_to_path",
            "revit_get_family_instances",
            "revit_list_family_types_in_family",
            "revit_list_loaded_families",
            "revit_load_family_from_path",
            "revit_rename_family_type",
            "revit_replace_family_type",
            "revit_unload_family"
        };

        [Fact]
        public void Every_families_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(FamiliesTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(FamiliesToolsInClass, declared);
        }

        [Fact]
        public async Task List_loaded_families_sends_snake_case_filters()
        {
            var sent = await Capture.Send(() => FamiliesTools.ListLoadedFamilies("Balusters", "loadable", true, 50));

            Assert.Equal("list_loaded_families", sent.Command);
            var json = sent.Json();
            Assert.Equal("Balusters", json.Value<string>("category_filter"));
            Assert.Equal("loadable", json.Value<string>("kind_filter"));
            Assert.True(json.Value<bool>("include_instance_count"));
            Assert.Equal(50, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Load_family_from_path_sends_timeout_separately()
        {
            var sent = await Capture.Send(() => FamiliesTools.LoadFamilyFromPath(@"C:\f.rfa", false, true, true, 100, 120));

            Assert.Equal("load_family_from_path", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\f.rfa", json.Value<string>("path"));
            Assert.False(json.Value<bool>("overwrite_existing"));
            Assert.True(json.Value<bool>("overwrite_parameter_values"));
            Assert.True(json.Value<bool>("include_symbols"));
            Assert.Equal(100, json.Value<int>("max_symbol_results"));
            Assert.Equal(120, sent.TimeoutSeconds);
        }

        [Fact]
        public async Task Unload_family_sends_string_family_id()
        {
            var sent = await Capture.Send(() => FamiliesTools.UnloadFamily("10271173", "", true, false));

            Assert.Equal("unload_family", sent.Command);
            var json = sent.Json();
            Assert.Equal(JTokenType.String, json["family_id"].Type);
            Assert.Equal("10271173", json.Value<string>("family_id"));
            Assert.True(json.Value<bool>("cascade_delete_instances"));
            Assert.False(json.Value<bool>("dry_run"));
        }

        [Fact]
        public async Task Duplicate_family_type_sends_string_type_id_and_parsed_overrides()
        {
            var sent = await Capture.Send(() => FamiliesTools.DuplicateFamilyType("9913571", "NewType", "{\"Mark\":\"X\"}"));

            Assert.Equal("duplicate_family_type", sent.Command);
            var json = sent.Json();
            Assert.Equal(JTokenType.String, json["source_type_id"].Type);
            Assert.Equal("9913571", json.Value<string>("source_type_id"));
            Assert.Equal("NewType", json.Value<string>("new_type_name"));
            Assert.Equal("X", json["type_parameter_overrides"].Value<string>("Mark"));
        }

        [Fact]
        public async Task Rename_family_type_sends_string_type_id()
        {
            var sent = await Capture.Send(() => FamiliesTools.RenameFamilyType("9913571", "T2"));

            Assert.Equal("rename_family_type", sent.Command);
            var json = sent.Json();
            Assert.Equal(JTokenType.String, json["type_id"].Type);
            Assert.Equal("9913571", json.Value<string>("type_id"));
            Assert.Equal("T2", json.Value<string>("new_name"));
        }

        [Fact]
        public async Task Audit_families_sends_sections_and_paging()
        {
            var sent = await Capture.Send(() => FamiliesTools.AuditFamilies(true, false, true, false, 30, 10, 40));

            Assert.Equal("audit_families", sent.Command);
            var json = sent.Json();
            Assert.True(json.Value<bool>("include_unused"));
            Assert.False(json.Value<bool>("include_inplace"));
            Assert.Equal(30, json.Value<int>("high_type_count_threshold"));
            Assert.Equal(10, json.Value<int>("start_index"));
            Assert.Equal(40, json.Value<int>("limit_per_section"));
        }

        [Fact]
        public async Task Replace_family_type_sends_string_ids_and_scope()
        {
            var sent = await Capture.Send(() => FamiliesTools.ReplaceFamilyType("1", "2", "view", 77, true));

            Assert.Equal("replace_family_type", sent.Command);
            var json = sent.Json();
            Assert.Equal(JTokenType.String, json["from_type_id"].Type);
            Assert.Equal("1", json.Value<string>("from_type_id"));
            Assert.Equal("2", json.Value<string>("to_type_id"));
            Assert.Equal("view", json.Value<string>("scope"));
            Assert.Equal(77, json.Value<long>("view_id"));
            Assert.True(json.Value<bool>("dry_run"));
        }

        [Fact]
        public async Task Get_family_instances_sends_string_family_id()
        {
            var sent = await Capture.Send(() => FamiliesTools.GetFamilyInstances("10271173", "", "76mm", true, 25));

            Assert.Equal("get_family_instances", sent.Command);
            var json = sent.Json();
            Assert.Equal(JTokenType.String, json["family_id"].Type);
            Assert.Equal("10271173", json.Value<string>("family_id"));
            Assert.Equal("76mm", json.Value<string>("type_name"));
            Assert.True(json.Value<bool>("view_only"));
            Assert.Equal(25, json.Value<int>("limit"));
        }

        [Fact]
        public async Task List_family_types_in_family_sends_string_id_and_paging()
        {
            var sent = await Capture.Send(() => FamiliesTools.ListFamilyTypesInFamily(
                "10271173", "", false, true, 5, 20, new[] { "Mark" }));

            Assert.Equal("list_family_types_in_family", sent.Command);
            var json = sent.Json();
            Assert.Equal(JTokenType.String, json["family_id"].Type);
            Assert.Equal("10271173", json.Value<string>("family_id"));
            Assert.False(json.Value<bool>("include_parameter_values"));
            Assert.True(json.Value<bool>("include_built_in_only"));
            Assert.Equal(5, json.Value<int>("start_index"));
            Assert.Equal(20, json.Value<int>("max_types"));
            Assert.Single(json["parameter_names"]);
        }

        [Fact]
        public async Task Export_family_to_path_sends_output_path_and_string_id()
        {
            var sent = await Capture.Send(() => FamiliesTools.ExportFamilyToPath(@"C:\out.rfa", "10271173", "", true));

            Assert.Equal("export_family_to_path", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\out.rfa", json.Value<string>("output_path"));
            Assert.Equal(JTokenType.String, json["family_id"].Type);
            Assert.Equal("10271173", json.Value<string>("family_id"));
            Assert.True(json.Value<bool>("overwrite_existing"));
        }
    }
}
