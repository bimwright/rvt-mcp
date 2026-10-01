using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolPermissionSemanticsTests
    {
        // MCP destructive=false means only additive updates, including the strongest
        // optional branch. Preview defaults and Revit Undo do not make a setter safe.
        private static readonly string[] DestructiveCommands =
        {
            "acquire_coordinates_from_link", "apply_filter_to_view", "apply_keynote_to_element",
            "apply_schedule_filter_sort", "apply_view_template", "assign_elements_to_workset",
            "assign_material_to_element", "assign_revision_to_sheet", "batch_execute",
            "bind_shared_parameter", "change_element_type", "clear_element_overrides", "color_elements",
            "connect_mep_elements", "delete_element", "delete_saved_selection", "delete_view_template",
            "dismiss_bake_suggestion", "load_family_from_path", "operate_element", "override_element_graphics",
            "publish_coordinates_to_link", "purge_unused", "reload_link", "remove_filter_from_view",
            "remove_parameter_binding", "rename_family_type", "renumber_sheets", "replace_family_type",
            "run_baked_tool", "save_selection", "set_category_visibility", "set_element_parameter_values",
            "set_element_phase", "set_filter_overrides", "set_material_appearance", "set_material_identity",
            "set_material_structural_asset", "set_material_thermal_asset", "set_parameter_value_by_guid",
            "set_project_base_point", "set_project_info", "set_structural_load", "set_system_classification",
            "set_titleblock_parameters", "set_type_parameter_values", "set_view_crop", "set_view_phase",
            "set_view_scale", "unload_family", "unload_link", "update_schedule_field", "wipe_empty_tags",
            "workflow_clash_review", "workflow_data_roundtrip", "workflow_naming_normalization",
            "workflow_sheet_set", "workflow_takeoff_report", "workflow_view_cleanup"
        };

        public static IEnumerable<object[]> DestructiveTools
            => DestructiveCommands.Select(command => new object[] { "revit_" + command });

        [Theory]
        [MemberData(nameof(DestructiveTools))]
        public void Existing_data_mutations_publish_destructive_hints(string name)
        {
            var method = Assert.Single(Tools(), m => m.GetCustomAttribute<McpServerToolAttribute>().Name == name);
            var tool = McpServerTool.Create(method, (object)null).ProtocolTool;
            Assert.False(tool.Annotations.ReadOnlyHint);
            Assert.True(tool.Annotations.DestructiveHint, name);
        }

        [Fact]
        public void Non_destructive_write_tools_are_additive_or_session_navigation()
        {
            var additivePrefixes = new[] { "create_", "duplicate_", "tag_", "place_", "add_", "auto_create_" };
            var otherAdditive = new HashSet<string>(StringComparer.Ordinal)
            {
                "compute_room_finishes", "export_room_data", "export_shared_parameter_file",
                "get_material_takeoff", "import_cad_to_view", "link_revit_model", "list_bake_suggestions",
                "open_model", "workflow_room_documentation"
            };
            foreach (var method in Tools())
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr.Name == "revit_send_code_to_revit" || attr.ReadOnly || attr.Destructive) continue;
                var command = attr.Name.Substring(6);
                Assert.True(additivePrefixes.Any(prefix => command.StartsWith(prefix, StringComparison.Ordinal))
                    || otherAdditive.Contains(command), attr.Name + " needs a strongest-branch permission review.");
            }
        }

        [Theory]
        [InlineData("revit_create_level")]
        [InlineData("revit_duplicate_view_template")]
        [InlineData("revit_tag_all_walls")]
        [InlineData("revit_export_room_data")]
        [InlineData("revit_open_model")]
        public void Additive_or_navigation_tools_do_not_gain_destructive_hints(string name)
        {
            var method = Assert.Single(Tools(), m => m.GetCustomAttribute<McpServerToolAttribute>().Name == name);
            Assert.False(McpServerTool.Create(method, (object)null).ProtocolTool.Annotations.DestructiveHint);
        }

        [Fact]
        public void Destructive_descriptions_explain_recovery_and_read_only_tools_remain_non_destructive()
        {
            foreach (var method in Tools())
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr.Name == "revit_send_code_to_revit") continue;
                if (attr.ReadOnly) Assert.False(attr.Destructive);
                if (!attr.Destructive) continue;
                var description = method.GetCustomAttribute<DescriptionAttribute>().Description;
                Assert.True(new[] { "Undo", "back up", "backup", "restore", "cannot be undone" }
                    .Any(word => description.Contains(word, StringComparison.OrdinalIgnoreCase)), attr.Name);
            }
        }

        private static MethodInfo[] Tools()
        {
            var config = new RvtMcpConfig { Toolsets = new List<string> { "all" }, EnableAdaptiveBake = true };
            return Program.ResolveRegisteredToolMethods(ToolsetFilter.Resolve(config), config).ToArray();
        }
    }
}
