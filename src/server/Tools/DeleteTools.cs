using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using RvtMcp.Server.Prompts;
using RvtMcp.ToolCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
