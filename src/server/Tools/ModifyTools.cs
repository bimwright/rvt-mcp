using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("modify")]
    public class ModifyTools
    {
        [McpServerTool(Name = "revit_move_elements", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Translate existing elements by dx_mm/dy_mm/dz_mm in model coordinates; restore with Revit Undo. Use for furniture placement and offsets. All three millimetre deltas are required. Rejects missing IDs, types, pinned elements and individual group members. Supports elements with a point or bounded curve location; constrained movement rolls back the entire selection. Returns actual before/after placement.")]
        public static async Task<string> MoveElements(long[] elementIds, double dx_mm, double dy_mm, double dz_mm)
            => await ToolGateway.CallAsync("move_elements", new { elementIds, dx_mm, dy_mm, dz_mm });

        [McpServerTool(Name = "revit_rotate_elements", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Rotate existing elements around an explicit vertical Z axis through pivot_x_mm/pivot_y_mm/pivot_z_mm in model coordinates; restore with Revit Undo. angle_degrees is positive counterclockwise when viewed from above. All pivot coordinates are required. Supports point or bounded curve locations. Invalid or constrained selections roll back atomically; returns actual before/after placement. Read the current location before choosing the pivot.")]
        public static async Task<string> RotateElements(long[] elementIds, double angle_degrees, double pivot_x_mm, double pivot_y_mm, double pivot_z_mm)
            => await ToolGateway.CallAsync("rotate_elements", new { elementIds, angle_degrees, pivot_x_mm, pivot_y_mm, pivot_z_mm });

        [McpServerTool(Name = "revit_operate_element", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change element visibility or graphics; restore document changes with Revit Undo. Select/hide/isolate/color elements in current view. operation: select (highlight), hide, unhide, isolate (hide everything else), setcolor (RGB override). elementIds: JSON int array e.g. '[12345, 67890]'. For setcolor: r/g/b 0-255 (default red 255,0,0).")]
        public static async Task<string> OperateElement(string operation, string elementIds, byte r = 255, byte g = 0, byte b = 0)
            => await ToolGateway.CallWithParamsAsync("operate_element", () =>
            {
                var parsedIds = JArray.Parse(elementIds).ToObject<long[]>();
                return new { operation, elementIds = parsedIds, r, g, b };
            });

        [McpServerTool(Name = "revit_color_elements", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Overwrite element graphics; restore them with Revit Undo. Color-code all matching elements by parameter value. maxGroups caps response detail only; mutation summary remains complete.")]
        public static async Task<string> ColorElements(string category, string parameterName, int maxGroups = 100)
            => await ToolGateway.CallAsync("color_elements", new { category, parameterName, max_groups = maxGroups });

        [McpServerTool(Name = "revit_set_element_parameter_values", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace instance parameter values; restore them with Revit Undo. Set an instance parameter on multiple elements. valueType can be auto/string/integer/double/elementId; length-like doubles use mm input.")]
        public static async Task<string> SetElementParameterValues(long[] elementIds, string parameterName, string value, string valueType = "auto")
            => await ToolGateway.CallAsync("set_element_parameter_values", new { elementIds, parameterName, value, valueType });

        [McpServerTool(Name = "revit_set_type_parameter_values", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace type parameter values; restore them with Revit Undo. Set a type parameter on explicit type ids or on the types resolved from element ids.")]
        public static async Task<string> SetTypeParameterValues(string parameterName, string value, long[] typeIds = null, long[] elementIds = null, string valueType = "auto")
            => await ToolGateway.CallAsync("set_type_parameter_values", new { parameterName, value, typeIds, elementIds, valueType });

        [McpServerTool(Name = "revit_change_element_type", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace element types; restore them with Revit Undo. Change one or more elements to a target ElementType id after validating type compatibility.")]
        public static async Task<string> ChangeElementType(long[] elementIds, long typeId)
            => await ToolGateway.CallAsync("change_element_type", new { elementIds, typeId });

        [McpServerTool(Name = "revit_assign_elements_to_workset", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace element workset assignments; restore them with Revit Undo. Assign elements to a user workset by worksetId or worksetName in a workshared document.")]
        public static async Task<string> AssignElementsToWorkset(long[] elementIds, long? worksetId = null, string worksetName = "")
            => await ToolGateway.CallAsync("assign_elements_to_workset", new { elementIds, worksetId, worksetName });

        [McpServerTool(Name = "revit_set_wall_top", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Replace walls' top constraint; restore it with Revit Undo. Attach walls to a level (Top Constraint: Up to level) with an optional top_offset in mm. " +
            "Use after revit_create_line_based_element, which only sets an unconnected height. Every wall is checked first: a non-wall id or a top at or below a wall's base changes nothing. " +
            "Returns the stored constraint, offset and height per wall. Example: wall_ids=[1234,1235], top_level='Roof', top_offset=0.")]
        public static async Task<string> SetWallTop(long[] wall_ids, string top_level, double top_offset = 0)
        {
            var blocked = ServerState.BlockIfReadOnly("set_wall_top");
            if (blocked != null) return blocked;
            return await ToolGateway.CallAsync("set_wall_top", new { wall_ids, top_level, top_offset });
        }
    }
}
