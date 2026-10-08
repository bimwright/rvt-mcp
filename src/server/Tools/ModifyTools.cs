using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("modify")]
    public class ModifyTools
    {
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
    }
}
