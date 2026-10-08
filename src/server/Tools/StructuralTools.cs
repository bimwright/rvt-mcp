using System;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("structural")]
    public class StructuralTools
    {
        [McpServerTool(Name = "revit_create_structural_column", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a structural column at a point. Params: type_id OR type_name (structural column family), x_mm/y_mm/z_mm (default 0), level_id OR level_name (default lowest level), height_mm (optional top offset), rotation_deg (optional, default 0).")]
        public static async Task<string> CreateStructuralColumn(
            long? type_id = null, string type_name = null,
            double x_mm = 0, double y_mm = 0, double z_mm = 0,
            long? level_id = null, string level_name = null,
            double? height_mm = null, double rotation_deg = 0)
            => await ToolGateway.CallAsync("create_structural_column", new {
                type_id, type_name, x_mm, y_mm, z_mm,
                level_id, level_name, height_mm, rotation_deg
            });

        [McpServerTool(Name = "revit_create_structural_beam", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a structural beam between two points. Params: type_id OR type_name (structural framing family), start_x_mm/start_y_mm/start_z_mm (required), end_x_mm/end_y_mm/end_z_mm (required), level_id OR level_name, usage ('beam'|'brace'|'joist', default 'beam').")]
        public static async Task<string> CreateStructuralBeam(
            double start_x_mm, double start_y_mm, double end_x_mm, double end_y_mm,
            long? type_id = null, string type_name = null,
            double start_z_mm = 0, double end_z_mm = 0,
            long? level_id = null, string level_name = null, string usage = "beam")
            => await ToolGateway.CallAsync("create_structural_beam", new {
                type_id, type_name, start_x_mm, start_y_mm, start_z_mm,
                end_x_mm, end_y_mm, end_z_mm, level_id, level_name, usage
            });

        [McpServerTool(Name = "revit_create_structural_wall", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a structural wall between two points. Params: start_x_mm/start_y_mm/end_x_mm/end_y_mm (required), wall_type_id OR wall_type_name (optional, default current), level_id OR level_name, height_mm (default 3000). Sets isStructural=true.")]
        public static async Task<string> CreateStructuralWall(
            double start_x_mm, double start_y_mm, double end_x_mm, double end_y_mm,
            long? wall_type_id = null, string wall_type_name = null,
            long? level_id = null, string level_name = null, double height_mm = 3000)
            => await ToolGateway.CallAsync("create_structural_wall", new {
                wall_type_id, wall_type_name, start_x_mm, start_y_mm,
                end_x_mm, end_y_mm, level_id, level_name, height_mm
            });

        [McpServerTool(Name = "revit_create_foundation_isolated", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an isolated/spread footing at a point or under an existing column. Params: type_id OR type_name (StructuralFoundation), x_mm/y_mm (required), z_mm (default 0), level_id OR level_name, host_column_id (optional — when supplied, location is taken from the column), rotation_deg.")]
        public static async Task<string> CreateFoundationIsolated(
            double x_mm, double y_mm,
            long? type_id = null, string type_name = null, double z_mm = 0,
            long? level_id = null, string level_name = null,
            long? host_column_id = null, double rotation_deg = 0)
            => await ToolGateway.CallAsync("create_foundation_isolated", new {
                type_id, type_name, x_mm, y_mm, z_mm,
                level_id, level_name, host_column_id, rotation_deg
            });

        [McpServerTool(Name = "revit_create_foundation_wall", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a wall foundation under an existing wall. Params: wall_id (required), foundation_type_id OR foundation_type_name (optional, defaults to first WallFoundation type).")]
        public static async Task<string> CreateFoundationWall(
            long wall_id,
            long? foundation_type_id = null, string foundation_type_name = null)
            => await ToolGateway.CallAsync("create_foundation_wall", new {
                wall_id, foundation_type_id, foundation_type_name
            });

        [McpServerTool(Name = "revit_list_rebar", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List rebar instances. Optional filters: host_id, view_id, limit (default 500). Returns id, bar_type, diameter_mm, quantity, layout_rule, host_id, host_category.")]
        public static async Task<string> ListRebar(long? host_id = null, long? view_id = null, int limit = 500)
            => await ToolGateway.CallAsync("list_rebar", new { host_id, view_id, limit });

        [McpServerTool(Name = "revit_get_structural_loads", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List structural loads (point, line, area). Filter by element_id (host) or load_type ('point'|'line'|'area'). Returns force/moment components per load.")]
        public static async Task<string> GetStructuralLoads(
            long? element_id = null, string load_type = null, int limit = 500)
            => await ToolGateway.CallAsync("get_structural_loads", new { element_id, load_type, limit });

        [McpServerTool(Name = "revit_set_structural_load", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace structural load values; restore them with Revit Undo. Update force/moment of an existing structural load. action='update' supported; action='create' returns not_implemented. Params: action ('update'), load_id (required for update), force_x/y/z, moment_x/y/z (optional, units = Revit internal).")]
        public static async Task<string> SetStructuralLoad(
            string action,
            long? load_id = null,
            double? force_x = null, double? force_y = null, double? force_z = null,
            double? moment_x = null, double? moment_y = null, double? moment_z = null)
        {
            if (string.Equals(action, "update", StringComparison.OrdinalIgnoreCase))
            {
                var blocked = ServerState.BlockIfReadOnly("set_structural_load");
                if (blocked != null) return blocked;
            }

            try
            {
                var result = await ToolGateway.SendToRevit("set_structural_load", new
                {
                    action,
                    load_id,
                    force_x,
                    force_y,
                    force_z,
                    moment_x,
                    moment_y,
                    moment_z
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_structural_connections", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Audit structural joins between columns and beams. Optional element_ids filter (default = all structural framing + columns). Returns joined_count and joined_with per element.")]
        public static async Task<string> AnalyzeStructuralConnections(
            long[] element_ids = null, int limit = 500)
            => await ToolGateway.CallAsync("analyze_structural_connections", new { element_ids, limit });

        [McpServerTool(Name = "revit_tag_structural_framing", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Place structural framing tags on beams in the active or specified view. Params: view_id (optional, default active view), tag_type_id (optional, default first StructuralFramingTags), element_ids (optional, default all framing in view).")]
        public static async Task<string> TagStructuralFraming(
            long? view_id = null, long? tag_type_id = null, long[] element_ids = null)
            => await ToolGateway.CallAsync("tag_structural_framing", new { view_id, tag_type_id, element_ids });

        [McpServerTool(Name = "revit_create_rebar_set", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a straight rebar set (single bar or arrayed) inside a structural host. Params: host_id (required), bar_type_id OR bar_type_name (optional, default first RebarBarType), layout_rule ('Single'|'FixedNumber'|'MaximumSpacing', default 'Single'), spacing_mm (required for FixedNumber/MaximumSpacing), quantity (required for FixedNumber, default 1), start_x_mm/start_y_mm/start_z_mm + end_x_mm/end_y_mm/end_z_mm (required). Uses Rebar.CreateFromCurves + RebarShapeDrivenAccessor.")]
        public static async Task<string> CreateRebarSet(
            long host_id,
            double start_x_mm, double start_y_mm, double start_z_mm,
            double end_x_mm, double end_y_mm, double end_z_mm,
            long? bar_type_id = null, string bar_type_name = null,
            string layout_rule = "Single",
            double? spacing_mm = null, int quantity = 1)
            => await ToolGateway.CallAsync("create_rebar_set", new
            {
                host_id, bar_type_id, bar_type_name, layout_rule, spacing_mm, quantity,
                start_x_mm, start_y_mm, start_z_mm, end_x_mm, end_y_mm, end_z_mm
            });

        [McpServerTool(Name = "revit_create_rebar_stirrup", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a shape-driven rebar (typically a closed stirrup) inside a concrete host. Params: host_id (required), bar_type_id OR bar_type_name (optional, default first RebarBarType), shape_id OR shape_name (optional, default first RebarShape — e.g. 'Stirrup T1', 'M_T1'), origin_x_mm/origin_y_mm/origin_z_mm (required), x_vec_x/y/z + y_vec_x/y/z (optional unit-less direction vectors, default world X/Y). Uses Rebar.CreateFromRebarShape.")]
        public static async Task<string> CreateRebarStirrup(
            long host_id,
            double origin_x_mm, double origin_y_mm, double origin_z_mm,
            long? bar_type_id = null, string bar_type_name = null,
            long? shape_id = null, string shape_name = null,
            double x_vec_x = 1, double x_vec_y = 0, double x_vec_z = 0,
            double y_vec_x = 0, double y_vec_y = 1, double y_vec_z = 0)
            => await ToolGateway.CallAsync("create_rebar_stirrup", new
            {
                host_id, bar_type_id, bar_type_name, shape_id, shape_name,
                origin_x_mm, origin_y_mm, origin_z_mm,
                x_vec_x, x_vec_y, x_vec_z, y_vec_x, y_vec_y, y_vec_z
            });
    }
}
