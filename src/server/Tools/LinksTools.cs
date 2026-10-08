using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("links")]
    public class LinksTools
    {
        [McpServerTool(Name = "revit_list_linked_models", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List external Revit linked models and instances.")]
        public static async Task<string> ListLinkedModels(bool includeInstances = true, bool includeUnloaded = true)
            => await ToolGateway.CallAsync("list_linked_models", new { include_instances = includeInstances, include_unloaded = includeUnloaded });

        [McpServerTool(Name = "revit_list_linked_cad", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List imported or linked CAD files in the model.")]
        public static async Task<string> ListLinkedCad(bool includeImports = true, bool includeLinks = true)
            => await ToolGateway.CallAsync("list_linked_cad", new { include_imports = includeImports, include_links = includeLinks });

        [McpServerTool(Name = "revit_import_cad_to_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Import or link a CAD drawing (.dwg, .dxf) into a specific view.")]
        public static async Task<string> ImportCadToView(string path, long? viewId = null, bool link = false, string placement = "origin", string unit = "default", bool thisViewOnly = true, bool visibleLayersOnly = true)
            => await ToolGateway.CallAsync("import_cad_to_view", new { path, view_id = viewId, link, placement, unit, this_view_only = thisViewOnly, visible_layers_only = visibleLayersOnly });

        [McpServerTool(Name = "revit_link_revit_model", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Link a Revit model (.rvt) into the project. timeout_seconds: plugin wait 1-900s, default 600 - linking a large model can exceed 60s.")]
        public static async Task<string> LinkRevitModel(string path, string placement = "origin", bool relative = false, bool reuseExistingType = false, int timeout_seconds = 600)
        {
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;

            return await ToolGateway.CallAsync("link_revit_model", new { path, placement, relative, reuse_existing_type = reuseExistingType }, timeout_seconds);
        }

        [McpServerTool(Name = "revit_unload_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Unload a Revit link type by type or instance ID." +
            " Changes link loading outside a document transaction. Save a model backup first; Revit Undo may not restore this operation.")]
        public static async Task<string> UnloadLink(long? linkTypeId = null, long? linkInstanceId = null, string scope = "all_users")
            => await ToolGateway.CallAsync("unload_link", new { link_type_id = linkTypeId, link_instance_id = linkInstanceId, scope });

        [McpServerTool(Name = "revit_reload_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Reload a Revit link type by type or instance ID. timeout_seconds: plugin wait 1-900s, default 600 - reloading a large link can exceed 60s." +
            " Changes link loading outside a document transaction. Save a model backup first; Revit Undo may not restore this operation.")]
        public static async Task<string> ReloadLink(long? linkTypeId = null, long? linkInstanceId = null, int timeout_seconds = 600)
        {
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;

            return await ToolGateway.CallAsync("reload_link", new { link_type_id = linkTypeId, link_instance_id = linkInstanceId }, timeout_seconds);
        }

        [McpServerTool(Name = "revit_get_link_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Retrieve elements from a Revit link instance document.")]
        public static async Task<string> GetLinkElements(long linkInstanceId, string category = "", int limit = 500, bool includeBoundingBox = false)
            => await ToolGateway.CallAsync("get_link_elements", new { link_instance_id = linkInstanceId, category, limit, include_bounding_box = includeBoundingBox });

        [McpServerTool(Name = "revit_get_project_coordinate_system", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read the complete project coordinate setup: immutable Internal Origin, Project Base Point, Survey Point, active and named Project Locations, coordinates under each location, angle to True North, and geographic site data. Lengths are returned in mm; angles and latitude/longitude in degrees.")]
        public static async Task<string> GetProjectCoordinateSystem()
            => await ToolGateway.CallAsync("get_project_coordinate_system", new { });

        [McpServerTool(Name = "revit_get_link_coordinate_system", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Inspect a Revit or CAD link's instance and total transforms and map its origins into host internal/shared coordinates. For a loaded Revit link, also returns its Internal Origin, Project Base Point, Survey Point, and all linked Project Locations with ids. Use the returned Project Location id as linkedProjectLocationId for revit_publish_coordinates_to_link.")]
        public static async Task<string> GetLinkCoordinateSystem(long linkInstanceId)
            => await ToolGateway.CallAsync("get_link_coordinate_system", new { link_instance_id = linkInstanceId });

        [McpServerTool(Name = "revit_acquire_coordinates_from_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace host shared coordinates; restore them with Revit Undo. Acquire shared coordinates from a Revit link or linked CAD instance into the host project. Requires confirm=true and modifies the host shared-coordinate system.")]
        public static async Task<string> AcquireCoordinatesFromLink(long linkInstanceId, bool confirm = false)
            => await ToolGateway.CallAsync("acquire_coordinates_from_link", new { link_instance_id = linkInstanceId, confirm });

        [McpServerTool(Name = "revit_publish_coordinates_to_link", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Publish host shared coordinates to a loaded Revit link. Call revit_get_link_coordinate_system first to discover linkedProjectLocationId; omit it to target the linked active Project Location. Requires confirm=true. CAD links are not supported." +
            " Publishes changes to the linked model. Back up that model first; restoring its prior file may be required.")]
        public static async Task<string> PublishCoordinatesToLink(long linkInstanceId, long? linkedProjectLocationId = null, bool confirm = false)
            => await ToolGateway.CallAsync("publish_coordinates_to_link", new { link_instance_id = linkInstanceId, linked_project_location_id = linkedProjectLocationId, confirm });

        [McpServerTool(Name = "revit_set_project_base_point", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace base point or survey point values; restore them with Revit Undo. Set project base point or survey point parameters.")]
        public static async Task<string> SetProjectBasePoint(double eastWest, double northSouth, double elevation = 0, double angleToTrueNorth = 0, string pointKind = "project_base_point", bool dryRun = false)
            => await ToolGateway.CallAsync("set_project_base_point", new { east_west = eastWest, north_south = northSouth, elevation, angle_to_true_north = angleToTrueNorth, point_kind = pointKind, dry_run = dryRun });
    }
}
