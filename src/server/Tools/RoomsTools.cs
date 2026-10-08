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
    [McpServerToolType, Toolset("rooms")]
    public class RoomsTools
    {
        [McpServerTool(Name = "revit_list_rooms", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all rooms in the model with status classification (placed, unplaced, not_enclosed) and optional level/phase filters.")]
        public static async Task<string> ListRooms(string levelName = "", string phaseName = "", string status = "all", bool includeParameters = false, int limit = 5000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_rooms", new { level_name = levelName, phase_name = phaseName, status, include_parameters = includeParameters, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_room_boundaries", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Report room boundary segments (points in mm) and their bounding elements.")]
        public static async Task<string> GetRoomBoundaries(long roomId, string boundaryLocation = "finish", bool includeBoundaryElements = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_room_boundaries", new { room_id = roomId, boundary_location = boundaryLocation, include_boundary_elements = includeBoundaryElements });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_room_openings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Retrieve doors and windows belonging to a room boundary.")]
        public static async Task<string> GetRoomOpenings(long roomId, bool includeDoors = true, bool includeWindows = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_room_openings", new { room_id = roomId, include_doors = includeDoors, include_windows = includeWindows });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_room_separator", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create room separator lines from an array of {x,y,z} points (mm).")]
        public static async Task<string> CreateRoomSeparator(object[] points, long? viewId = null, string levelName = "", bool closeLoop = false)
        {
            try
            {
                var parsedPoints = McpJsonInput.RequiredArray(points, nameof(points));
                var result = await ToolGateway.SendToRevit("create_room_separator", new { points = parsedPoints, view_id = viewId, level_name = levelName, close_loop = closeLoop });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_area", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an area element at specified coordinates (mm) in an area plan.")]
        public static async Task<string> CreateArea(double x, double y, long? areaPlanViewId = null, string areaPlanViewName = "", string areaSchemeName = "", string levelName = "", bool createAreaPlanIfMissing = false, string name = "", string number = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_area", new { x, y, area_plan_view_id = areaPlanViewId, area_plan_view_name = areaPlanViewName, area_scheme_name = areaSchemeName, level_name = levelName, create_area_plan_if_missing = createAreaPlanIfMissing, name, number });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_space", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create an MEP space element at specified coordinates (mm) on a target level.")]
        public static async Task<string> CreateSpace(double x, double y, string levelName = "", string phaseName = "", string name = "", string number = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_space", new { x, y, level_name = levelName, phase_name = phaseName, name, number });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_areas", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List area elements in the model with optional scheme/level filters.")]
        public static async Task<string> ListAreas(string areaSchemeName = "", string levelName = "", string status = "all", int limit = 5000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_areas", new { area_scheme_name = areaSchemeName, level_name = levelName, status, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_compute_room_finishes", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Calculate room finishes and boundary materials. output=file writes relational SQLite to a local same-machine absolute path with schema and bounded preview; remote clients receive preview only.")]
        public static async Task<string> ComputeRoomFinishes(long[] roomIds = null, string levelName = "", bool includeEmpty = true, int limit = 5000, string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("compute_room_finishes", new { room_ids = roomIds, level_name = levelName, include_empty = includeEmpty, limit, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_auto_create_rooms_from_walls", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Automatically generate room elements in all enclosed boundary circuits found on the target level.")]
        public static async Task<string> AutoCreateRoomsFromWalls(string levelName, string phaseName = "", string namePrefix = "", string numberPrefix = "", int startNumber = 1, bool dryRun = true, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("auto_create_rooms_from_walls", new { level_name = levelName, phase_name = phaseName, name_prefix = namePrefix, number_prefix = numberPrefix, start_number = startNumber, dry_run = dryRun, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_tag_all_areas", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Place area tags for a bounded batch of untagged areas. limit hard max 5000; response is a compact mutation summary.")]
        public static async Task<string> TagAllAreas(long? areaPlanViewId = null, string areaPlanViewName = "", bool skipExisting = true, long? tagTypeId = null, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_all_areas", new { area_plan_view_id = areaPlanViewId, area_plan_view_name = areaPlanViewName, skip_existing = skipExisting, tag_type_id = tagTypeId, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
