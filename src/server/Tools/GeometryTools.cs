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
    [McpServerToolType, Toolset("geometry")]
    public class GeometryTools
    {
        [McpServerTool(Name = "revit_get_element_bounding_box", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get the bounding box of one or more elements, optionally relative to a view, and optionally including transform data")]
        public static async Task<string> GetElementBoundingBox(long[] elementIds, long? viewId = null, bool includeTransform = false)
            => await ToolGateway.CallAsync("get_element_bounding_box", new
            {
                element_ids = elementIds,
                view_id = viewId,
                include_transform = includeTransform
            });

        [McpServerTool(Name = "revit_get_element_geometry", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get geometric details and optional vertex samples for one or more elements")]
        public static async Task<string> GetElementGeometry(long[] elementIds, string detailLevel = "Medium", bool includeSamples = false, int sampleLimit = 20)
            => await ToolGateway.CallAsync("get_element_geometry", new
            {
                element_ids = elementIds,
                detail_level = detailLevel,
                include_samples = includeSamples,
                sample_limit = sampleLimit
            });

        [McpServerTool(Name = "revit_measure_distance_between_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Measure shortest distance between two elements based on bounding boxes, element locations, or bounding box pre-filter")]
        public static async Task<string> MeasureDistanceBetweenElements(long elementId1, long elementId2, string strategy = "bbox")
            => await ToolGateway.CallAsync("measure_distance_between_elements", new
            {
                element_id_1 = elementId1,
                element_id_2 = elementId2,
                strategy
            });

        [McpServerTool(Name = "revit_clash_detection", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Detect clashes between elements of category A and category B. Scope with viewId and keep maxPairs low. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ClashDetection(string[] categoriesA, string[] categoriesB, long? viewId = null, string strategy = "bbox_then_solid", int maxPairs = 1000, int maxResults = 100)
            => await ToolGateway.CallAsync("clash_detection", new
            {
                categories_a = categoriesA,
                categories_b = categoriesB,
                view_id = viewId,
                strategy,
                max_pairs = maxPairs,
                max_results = maxResults
            });

        [McpServerTool(Name = "revit_raycast_from_point", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Cast a ray from a 3D origin point in a given direction to find the nearest element hit within a 3D view")]
        public static async Task<string> RaycastFromPoint(double x, double y, double z, double dirX, double dirY, double dirZ, long view3dId, string[] categories = null, double maxDistance = 100000.0)
            => await ToolGateway.CallAsync("raycast_from_point", new
            {
                x,
                y,
                z,
                dir_x = dirX,
                dir_y = dirY,
                dir_z = dirZ,
                view_3d_id = view3dId,
                categories,
                max_distance = maxDistance
            });

        [McpServerTool(Name = "revit_find_elements_in_volume", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Find elements inside or intersecting an axis-aligned 3D volume or a room's bounding box")]
        public static async Task<string> FindElementsInVolume(object volume = null, long? roomId = null, string[] categories = null, long? viewId = null, string match = "intersects", int limit = 200)
        {
            try
            {
                var parsedVolume = McpJsonInput.OptionalObject(volume, nameof(volume));
                var result = await ToolGateway.SendToRevit("find_elements_in_volume", new
                {
                    volume = parsedVolume,
                    room_id = roomId,
                    categories,
                    view_id = viewId,
                    match,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_compute_element_volume", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Compute the geometric solid volume of one or more elements in cubic meters (m3)")]
        public static async Task<string> ComputeElementVolume(long[] elementIds, string detailLevel = "Medium")
            => await ToolGateway.CallAsync("compute_element_volume", new
            {
                element_ids = elementIds,
                detail_level = detailLevel
            });

        [McpServerTool(Name = "revit_compute_element_area", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Compute the total face area of one or more elements in square meters (m2)")]
        public static async Task<string> ComputeElementArea(long[] elementIds, string detailLevel = "Medium")
            => await ToolGateway.CallAsync("compute_element_area", new
            {
                element_ids = elementIds,
                detail_level = detailLevel
            });

        [McpServerTool(Name = "revit_project_point_onto_face", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Project a 3D point onto a specific face of a Revit element")]
        public static async Task<string> ProjectPointOntoFace(long elementId, double x, double y, double z, int faceIndex = 0, string detailLevel = "Medium")
            => await ToolGateway.CallAsync("project_point_onto_face", new
            {
                element_id = elementId,
                x,
                y,
                z,
                face_index = faceIndex,
                detail_level = detailLevel
            });

        [McpServerTool(Name = "revit_find_overlapping_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Find potentially overlapping elements of the same category, using bounding box intersection analysis")]
        public static async Task<string> FindOverlappingElements(string category, long? viewId = null, int maxPairs = 1000, int maxResults = 100)
            => await ToolGateway.CallAsync("find_overlapping_elements", new
            {
                category,
                view_id = viewId,
                max_pairs = maxPairs,
                max_results = maxResults
            });

        [McpServerTool(Name = "revit_get_element_centroid", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Compute the geometric centroid of one or more elements")]
        public static async Task<string> GetElementCentroid(long[] elementIds, string strategy = "solid_then_bbox")
            => await ToolGateway.CallAsync("get_element_centroid", new
            {
                element_ids = elementIds,
                strategy
            });

        [McpServerTool(Name = "revit_analyze_geometry_complexity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Infer geometry complexity based on solid/face count and complexity metrics")]
        public static async Task<string> AnalyzeGeometryComplexity(long[] elementIds = null, string[] categories = null, long? viewId = null, string detailLevel = "Medium", int limit = 200)
            => await ToolGateway.CallAsync("analyze_geometry_complexity", new
            {
                element_ids = elementIds,
                categories,
                view_id = viewId,
                detail_level = detailLevel,
                limit
            });
    }
}
