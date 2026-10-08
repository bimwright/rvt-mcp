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
    [McpServerToolType, Toolset("annotation")]
    public class AnnotationTools
    {
        [McpServerTool(Name = "revit_tag_all_walls", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Tag all walls in current view at midpoint. Skips already-tagged walls. Returns count of new tags.")]
        public static async Task<string> TagAllWalls()
            => await ToolGateway.CallAsync("tag_all_walls");

        [McpServerTool(Name = "revit_tag_all_rooms", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Tag all rooms in current view at location point. Skips already-tagged rooms. Returns count of new tags.")]
        public static async Task<string> TagAllRooms()
            => await ToolGateway.CallAsync("tag_all_rooms");

        [McpServerTool(Name = "revit_tag_elements", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Tag one or more elements in a view.")]
        public static async Task<string> TagElements(long[] elementIds, long? viewId = null, long? tagTypeId = null, string orientation = "Horizontal", bool leader = false, double offsetX = 0, double offsetY = 0)
            => await ToolGateway.CallAsync("tag_elements", new { element_ids = elementIds, view_id = viewId, tag_type_id = tagTypeId, orientation, leader, offset_x = offsetX, offset_y = offsetY });

        [McpServerTool(Name = "revit_tag_all_by_category", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Tag all elements of a specific category in a view.")]
        public static async Task<string> TagAllByCategory(string category, long? viewId = null, long? tagTypeId = null, bool skipExisting = true, bool leader = false, bool dryRun = false, int limit = 200)
            => await ToolGateway.CallAsync("tag_all_by_category", new { category, view_id = viewId, tag_type_id = tagTypeId, skip_existing = skipExisting, leader, dry_run = dryRun, limit });

        [McpServerTool(Name = "revit_create_text_note", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a text note element in a view.")]
        public static async Task<string> CreateTextNote(string text, double x, double y, long? viewId = null, long? textTypeId = null, double width = 0, double rotationDeg = 0)
            => await ToolGateway.CallAsync("create_text_note", new { text, x, y, view_id = viewId, text_type_id = textTypeId, width, rotation_deg = rotationDeg });

        [McpServerTool(Name = "revit_create_dimensions", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a dimension element in a view. references: array of Revit element-reference objects.")]
        public static async Task<string> CreateDimensions(object[] references, long? viewId = null, long? dimensionTypeId = null, object line = null)
        {
            try
            {
                var parsedReferences = McpJsonInput.RequiredArray(references, nameof(references));
                var parsedLine = McpJsonInput.OptionalObject(line, nameof(line));
                var result = await ToolGateway.SendToRevit("create_dimensions", new { references = parsedReferences, view_id = viewId, dimension_type_id = dimensionTypeId, line = parsedLine });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_filled_region", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a filled region element in a view. points: array of {x,y,z} point objects (mm).")]
        public static async Task<string> CreateFilledRegion(object[] points, long? viewId = null, long? filledRegionTypeId = null)
        {
            try
            {
                var parsedPoints = McpJsonInput.RequiredArray(points, nameof(points));
                var result = await ToolGateway.SendToRevit("create_filled_region", new { points = parsedPoints, view_id = viewId, filled_region_type_id = filledRegionTypeId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_detail_line", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a detail line element in a view.")]
        public static async Task<string> CreateDetailLine(double startX, double startY, double endX, double endY, long? viewId = null, long? lineStyleId = null)
            => await ToolGateway.CallAsync("create_detail_line", new { start_x = startX, start_y = startY, end_x = endX, end_y = endY, view_id = viewId, line_style_id = lineStyleId });

        [McpServerTool(Name = "revit_create_callout_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a callout view in a parent view.")]
        public static async Task<string> CreateCalloutView(long parentViewId, double minX, double minY, double maxX, double maxY, long? viewFamilyTypeId = null, string name = "")
            => await ToolGateway.CallAsync("create_callout_view", new { parent_view_id = parentViewId, min_x = minX, min_y = minY, max_x = maxX, max_y = maxY, view_family_type_id = viewFamilyTypeId, name });

        [McpServerTool(Name = "revit_list_keynotes", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all keynotes loaded in the model with prefix and search filters.")]
        public static async Task<string> ListKeynotes(string keyPrefix = "", string search = "", int limit = 200)
            => await ToolGateway.CallAsync("list_keynotes", new { key_prefix = keyPrefix, search, limit });

        [McpServerTool(Name = "revit_apply_keynote_to_element", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace keynote values; restore them with Revit Undo. Apply keynote parameter values to one or more elements.")]
        public static async Task<string> ApplyKeynoteToElement(long[] elementIds, string keynote, bool dryRun = false)
            => await ToolGateway.CallAsync("apply_keynote_to_element", new { element_ids = elementIds, keynote, dry_run = dryRun });

        [McpServerTool(Name = "revit_find_untagged_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Find visible elements of a specific category in a view that do not have tags.")]
        public static async Task<string> FindUntaggedElements(string category, long? viewId = null, int limit = 200)
            => await ToolGateway.CallAsync("find_untagged_elements", new { category, view_id = viewId, limit });

        [McpServerTool(Name = "revit_find_undimensioned_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Find visible elements of a specific category in a view that do not have dimensions.")]
        public static async Task<string> FindUndimensionedElements(string category, long? viewId = null, int limit = 200)
            => await ToolGateway.CallAsync("find_undimensioned_elements", new { category, view_id = viewId, limit });

        [McpServerTool(Name = "revit_wipe_empty_tags", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Delete empty tags; restore them with Revit Undo. Find and delete empty tags in a view.")]
        public static async Task<string> WipeEmptyTags(long? viewId = null, bool dryRun = true, int limit = 200)
            => await ToolGateway.CallAsync("wipe_empty_tags", new { view_id = viewId, dry_run = dryRun, limit });
    }
}
