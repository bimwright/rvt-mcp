using System;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("delete")]
    public class DeleteTools
    {
        [McpServerTool(Name = "revit_delete_element", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Delete elements by ID; restore them with Revit Undo in Revit (no MCP undo tool). elementIds: JSON int array e.g. '[12345, 67890]'. Fetch IDs from get_selected_elements or ai_element_filter first.")]
        public static async Task<string> DeleteElement(string elementIds)
        {
            try
            {
                var parsedIds = JArray.Parse(elementIds).ToObject<long[]>();
                var result = await ToolGateway.SendToRevit("delete_element", new { elementIds = parsedIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
