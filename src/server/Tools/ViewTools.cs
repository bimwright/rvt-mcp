using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("view")]
    public class ViewTools
    {
        [McpServerTool(Name = "revit_create_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a view (floorplan or 3d). Params: viewType ('floorplan' or '3d'), level (name, required for floorplan), name (optional).")]
        public static async Task<string> CreateView(string viewType, string level = "", string name = "")
            => await ToolGateway.CallAsync("create_view", new { viewType, level, name });

        [McpServerTool(Name = "revit_place_view_on_sheet", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Place a view on a sheet. Auto-creates sheet if sheetId omitted. Params: viewId (required), sheetId (optional), sheetNumber (optional), sheetName (optional).")]
        public static async Task<string> PlaceViewOnSheet(long viewId, long? sheetId = null, string sheetNumber = "", string sheetName = "MCP Generated Sheet")
            => await ToolGateway.CallAsync("place_view_on_sheet", new { viewId, sheetId, sheetNumber, sheetName });

        [McpServerTool(Name = "revit_analyze_sheet_layout", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Analyze title block and a bounded viewport page in mm. Use startViewport + maxViewports (hard max 500).")]
        public static async Task<string> AnalyzeSheetLayout(string sheetNumber = "", long? sheetId = null, int startViewport = 0, int maxViewports = 100)
            => await ToolGateway.CallAsync("analyze_sheet_layout", new { sheetNumber, sheetId, start_viewport = startViewport, max_viewports = maxViewports });

        [McpServerTool(Name = "revit_capture_view_image", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Export the active or specified view as png/jpeg (default 1600 pixels), optionally to an absolute output_path inside %TEMP% or %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\captures\\. " +
            "May overwrite files that Revit Undo cannot restore; back up the destination first.")]
        public static async Task<string> CaptureViewImage(
            string output_path = null,
            long? view_id = null, int pixel_size = 1600, string image_format = "png")
        {
            var blocked = ServerState.BlockIfReadOnly("capture_view_image");
            if (blocked != null) return blocked;

            return await ToolGateway.CallAsync("capture_view_image", new
            {
                view_id,
                output_path,
                pixel_size,
                image_format
            });
        }

        [McpServerTool(Name = "revit_set_view_crop", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace saved view crop settings; restore them with Revit Undo. Modify view crop: enabled, visible, explicit bounds_json (mm), or fit_element_ids with padding_mm. Params: view_id (optional, default active), enabled, visible, bounds_json, fit_element_ids, padding_mm (default 100).")]
        public static async Task<string> SetViewCrop(
            long? view_id = null, bool? enabled = null, bool? visible = null,
            string bounds_json = null, long[] fit_element_ids = null, double padding_mm = 100)
        {
            var blocked = ServerState.BlockIfReadOnly("set_view_crop");
            if (blocked != null) return blocked;

            return await ToolGateway.CallWithParamsAsync("set_view_crop", () =>
            {
                object boundsObj = null;
                if (!string.IsNullOrWhiteSpace(bounds_json))
                    boundsObj = JObject.Parse(bounds_json);
                return new
                {
                    view_id,
                    enabled,
                    visible,
                    bounds = boundsObj,
                    fit_element_ids,
                    padding_mm
                };
            });
        }

        [McpServerTool(Name = "revit_set_view_scale", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace a saved view scale; restore it with Revit Undo. Set the graphical scale denominator of a view (e.g., 50 for 1:50). Params: view_id (optional, default active), scale (required, positive integer).")]
        public static async Task<string> SetViewScale(int scale, long? view_id = null)
        {
            var blocked = ServerState.BlockIfReadOnly("set_view_scale");
            if (blocked != null) return blocked;

            return await ToolGateway.CallAsync("set_view_scale", new { view_id, scale });
        }

        [McpServerTool(Name = "revit_activate_view", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Set the active view in Revit UI. UI-only operation. Params: view_id OR view_name (required).")]
        public static async Task<string> ActivateView(long? view_id = null, string view_name = null)
        {
            var blocked = ServerState.BlockIfReadOnly("activate_view");
            if (blocked != null) return blocked;

            return await ToolGateway.CallAsync("activate_view", new { view_id, view_name });
        }

        [McpServerTool(Name = "revit_show_element_in_view", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Activate view, select elements, zoom to elements. UI-only. Params: element_ids (required), view_id (optional), activate_view (default true), select (default true), zoom (default true).")]
        public static async Task<string> ShowElementInView(
            long[] element_ids,
            long? view_id = null, bool activate_view = true, bool select = true, bool zoom = true)
        {
            var blocked = ServerState.BlockIfReadOnly("show_element_in_view");
            if (blocked != null) return blocked;

            return await ToolGateway.CallAsync("show_element_in_view", new
            {
                element_ids,
                view_id,
                activate_view,
                select,
                zoom
            });
        }

        [McpServerTool(Name = "revit_set_view_style", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Replace a view's display style and detail level; restore them with Revit Undo. Use before revit_capture_view_image to get shaded or realistic images. " +
            "display_style: wireframe|hidden_line|shaded|shaded_edges|consistent_colors|realistic; detail_level: coarse|medium|fine. View by view_id or view_name; default active view. " +
            "For category visibility use revit_set_category_visibility. Example: view_name='3D Overview', display_style='shaded', detail_level='fine'.")]
        public static async Task<string> SetViewStyle(long? view_id = null, string view_name = null, string display_style = null, string detail_level = null)
        {
            var blocked = ServerState.BlockIfReadOnly("set_view_style");
            if (blocked != null) return blocked;
            return await ToolGateway.CallAsync("set_view_style", new { view_id, view_name, display_style, detail_level });
        }

        [McpServerTool(Name = "revit_set_view_orientation", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Replace a 3D view's camera orientation; restore it with Revit Undo. The default 3D view ({3D}, or {3D - user} when workshared) only moves its temporary camera, which is not saved and has no Undo. " +
            "Pass view_name or view_id; without them the active view is used. To capture the result, e.g. for turntable images, pass the returned view.view_id to revit_capture_view_image, which otherwise exports the active view. " +
            "eye and target are [x,y,z] in mm; up defaults to +Z and falls back to another axis when parallel to the view direction. perspective=true toggles an isometric view to perspective (false toggles back) when Revit allows. " +
            "Zooms to fit if the view is open. The default 3D view's crop box is turned off; a named view keeps its crop box unless fit=true; fit=false changes neither. Example: view_name='{3D}', eye=[-50000,-50000,30000], target=[0,0,0].")]
        public static async Task<string> SetViewOrientation(double[] eye, double[] target, long? view_id = null, string view_name = null, double[] up = null,
            bool? perspective = null, bool? fit = null)
        {
            var blocked = ServerState.BlockIfReadOnly("set_view_orientation");
            if (blocked != null) return blocked;
            return await ToolGateway.CallAsync("set_view_orientation", new { view_id, view_name, eye, target, up, perspective, fit });
        }

        [McpServerTool(Name = "revit_create_camera_view", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Create a perspective 3D camera view, or replace the orientation of an existing perspective view with the same name; restore it with Revit Undo. " +
            "Use for eye-level or aerial perspective images with revit_capture_view_image. eye and target are [x,y,z] in mm, optional up. Far clipping is turned off unless far_clip=true. " +
            "Returns the view id. Example: name='Entrance Camera', eye=[0,-30000,1700], target=[0,0,1700].")]
        public static async Task<string> CreateCameraView(string name, double[] eye, double[] target, double[] up = null, bool far_clip = false)
        {
            var blocked = ServerState.BlockIfReadOnly("create_camera_view");
            if (blocked != null) return blocked;
            return await ToolGateway.CallAsync("create_camera_view", new { name, eye, target, up, far_clip });
        }
    }
}
