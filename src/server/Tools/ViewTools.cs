using System;
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

        [McpServerTool(Name = "revit_capture_view_image", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export a view to a raster image. output_path is optional; if provided it must be absolute and inside %TEMP% or %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\captures\\. Params: view_id (optional, default active), output_path (optional, defaults under captures), pixel_size (default 1600), image_format ('png'|'jpeg', default 'png')." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> CaptureViewImage(
            string output_path = null,
            long? view_id = null, int pixel_size = 1600, string image_format = "png")
        {
            var blocked = ServerState.BlockIfReadOnly("capture_view_image");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("capture_view_image", new
                {
                    view_id,
                    output_path,
                    pixel_size,
                    image_format
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_view_crop", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace saved view crop settings; restore them with Revit Undo. Modify view crop: enabled, visible, explicit bounds_json (mm), or fit_element_ids with padding_mm. Params: view_id (optional, default active), enabled, visible, bounds_json, fit_element_ids, padding_mm (default 100).")]
        public static async Task<string> SetViewCrop(
            long? view_id = null, bool? enabled = null, bool? visible = null,
            string bounds_json = null, long[] fit_element_ids = null, double padding_mm = 100)
        {
            var blocked = ServerState.BlockIfReadOnly("set_view_crop");
            if (blocked != null) return blocked;

            try
            {
                object boundsObj = null;
                if (!string.IsNullOrWhiteSpace(bounds_json))
                    boundsObj = JObject.Parse(bounds_json);

                var result = await ToolGateway.SendToRevit("set_view_crop", new
                {
                    view_id,
                    enabled,
                    visible,
                    bounds = boundsObj,
                    fit_element_ids,
                    padding_mm
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_view_scale", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace a saved view scale; restore it with Revit Undo. Set the graphical scale denominator of a view (e.g., 50 for 1:50). Params: view_id (optional, default active), scale (required, positive integer).")]
        public static async Task<string> SetViewScale(int scale, long? view_id = null)
        {
            var blocked = ServerState.BlockIfReadOnly("set_view_scale");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("set_view_scale", new { view_id, scale });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_activate_view", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Set the active view in Revit UI. UI-only operation. Params: view_id OR view_name (required).")]
        public static async Task<string> ActivateView(long? view_id = null, string view_name = null)
        {
            var blocked = ServerState.BlockIfReadOnly("activate_view");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("activate_view", new { view_id, view_name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_show_element_in_view", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Activate view, select elements, zoom to elements. UI-only. Params: element_ids (required), view_id (optional), activate_view (default true), select (default true), zoom (default true).")]
        public static async Task<string> ShowElementInView(
            long[] element_ids,
            long? view_id = null, bool activate_view = true, bool select = true, bool zoom = true)
        {
            var blocked = ServerState.BlockIfReadOnly("show_element_in_view");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("show_element_in_view", new
                {
                    element_ids,
                    view_id,
                    activate_view,
                    select,
                    zoom
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
