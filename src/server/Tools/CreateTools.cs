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
    [McpServerToolType, Toolset("create")]
    public class CreateTools
    {
        [McpServerTool(Name = "revit_create_line_based_element", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a line-based element (wall). Params: elementType, startX/Y, endX/Y (mm), level (name), typeId (optional), height (mm, default 3000).")]
        public static async Task<string> CreateLineBasedElement(string elementType, double startX, double startY, double endX, double endY, string level = "", long? typeId = null, double height = 3000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_line_based_element", new { elementType, startX, startY, endX, endY, level, typeId, height });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_point_based_element", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a OneLevelBased or OneLevelBasedHosted family (door, window, furniture). typeId from get_available_family_types; x/y/z are model coordinates in mm; level is a name. Hosted families require host_id of the intended local host; no host is inferred. Non-hosted families ignore host_id with a warning. Work-plane/face and other placement types are unsupported. Validates actual host and position within 1 mm before commit; returns placement_type, actual host_id and location_mm.")]
        public static async Task<string> CreatePointBasedElement(long typeId, double x, double y, double z = 0, string level = "", long? host_id = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_point_based_element", new { typeId, x, y, z, level, host_id });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_surface_based_element", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a surface-based element (floor, ceiling). Params: elementType, points (JSON array of {x,y} in mm, min 3), level (name), typeId (optional). Example points: [{\"x\":0,\"y\":0},{\"x\":6000,\"y\":0},{\"x\":6000,\"y\":4000},{\"x\":0,\"y\":4000}].")]
        public static async Task<string> CreateSurfaceBasedElement(string elementType, string points, string level = "", long? typeId = null)
        {
            try
            {
                var parsedPoints = JArray.Parse(points);
                var result = await ToolGateway.SendToRevit("create_surface_based_element", new { elementType, points = parsedPoints, level, typeId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_level", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a level at specified elevation. Params: elevation (mm), name (optional).")]
        public static async Task<string> CreateLevel(double elevation, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_level", new { elevation, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_grid", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a grid line. Params: startX/Y, endX/Y (mm), name (optional).")]
        public static async Task<string> CreateGrid(double startX, double startY, double endX, double endY, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_grid", new { startX, startY, endX, endY, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_room", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create and place a room. Params: x/y (mm), level (name), name (optional), number (optional).")]
        public static async Task<string> CreateRoom(double x, double y, string level = "", string name = "", string number = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_room", new { x, y, level, name, number });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_group_from_elements", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a Revit group from two or more element ids. Optional name renames the generated group type.")]
        public static async Task<string> CreateGroupFromElements(long[] elementIds, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_group_from_elements", new { elementIds, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
