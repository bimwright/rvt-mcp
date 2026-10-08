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
    [McpServerToolType, Toolset("export")]
    public class ExportTools
    {
        [McpServerTool(Name = "revit_export_room_data", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export all rooms. output=inline returns DTO data; output=file writes SQLite to a local same-machine absolute path with schema and bounded preview. Remote clients can use preview but cannot read the local file.")]
        public static async Task<string> ExportRoomData(string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_room_data", new { output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_pdf", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export sheets or views to PDF. outputFolder must be an existing absolute path. viewIds defaults to the active view. combine=true produces one combined PDF. Do not retry on 60s timeout; Revit may still be running the command." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportPdf(string outputFolder, long[] viewIds = null, bool combine = false, string fileName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_pdf", new { output_folder = outputFolder, view_ids = viewIds, combine, file_name = fileName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_dwg", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export sheets or views to AutoCAD DWG. outputFolder must be an existing absolute path. viewIds defaults to the active view. settingsName optionally selects a saved ExportDWGSettings. Do not retry on 60s timeout; Revit may still be running the command." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportDwg(string outputFolder, long[] viewIds = null, string settingsName = "", string fileNamePrefix = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dwg", new { output_folder = outputFolder, view_ids = viewIds, settings_name = settingsName, file_name_prefix = fileNamePrefix });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_dgn", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export sheets or views to MicroStation DGN. outputFolder must be an existing absolute path. viewIds defaults to the active view. Do not retry on 60s timeout; Revit may still be running the command." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportDgn(string outputFolder, long[] viewIds = null, string fileNamePrefix = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dgn", new { output_folder = outputFolder, view_ids = viewIds, file_name_prefix = fileNamePrefix });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_dwf", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export sheets or views to Autodesk DWF/DWFx. outputFolder must be an existing absolute path. viewIds defaults to the active view. useDwfx=true exports DWFx. Do not retry on 60s timeout; Revit may still be running the command." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportDwf(string outputFolder, long[] viewIds = null, string fileName = "", bool useDwfx = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dwf", new { output_folder = outputFolder, view_ids = viewIds, file_name = fileName, use_dwfx = useDwfx });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_ifc", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export the model to IFC. outputFolder must be an existing absolute path. ifcVersion: IFC2x3|IFC4|default. timeout_seconds: plugin wait 1-900s, default 600; a large model export can exceed 60s. Do not retry after a timeout; Revit may still be running the command." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportIfc(string outputFolder, string fileName, string ifcVersion = "default", int timeout_seconds = 600)
        {
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;

            try
            {
                var result = await ToolGateway.SendToRevit("export_ifc", new { output_folder = outputFolder, file_name = fileName, ifc_version = ifcVersion }, timeout_seconds);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_nwc", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export the model to Navisworks NWC. outputFolder must be an existing absolute path. Optional exportScopeViewId scopes the export to one view. Requires the Navisworks NWC exporter add-in installed. Do not retry on 60s timeout; Revit may still be running the command." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportNwc(string outputFolder, string fileName, long? exportScopeViewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_nwc", new { output_folder = outputFolder, file_name = fileName, export_scope_view_id = exportScopeViewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_fbx", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export a 3D view to Autodesk FBX. outputFolder must be an existing absolute path. viewId must reference a 3D view (defaults to the active view, which must be 3D)." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportFbx(string outputFolder, string fileName, long? viewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_fbx", new { output_folder = outputFolder, file_name = fileName, view_id = viewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_gbxml", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export the model's energy analytical data to gbXML. outputFolder must be an existing absolute path. Requires rooms/spaces with energy settings. Do not retry on 60s timeout; Revit may still be running the command." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportGbxml(string outputFolder, string fileName)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_gbxml", new { output_folder = outputFolder, file_name = fileName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_image", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export a view to a raster image. outputPath is an absolute file path (.png/.jpg). viewId defaults to the active view. pixelSize sets the longer dimension. imageFormat: png|jpeg." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportImage(string outputPath, long? viewId = null, int pixelSize = 2048, string imageFormat = "png")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_image", new { output_path = outputPath, view_id = viewId, pixel_size = pixelSize, image_format = imageFormat });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_schedule_csv", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export a Revit schedule's data to a delimited text/CSV file. outputPath is an absolute file path. Identify the schedule by scheduleId or scheduleName." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportScheduleCsv(string outputPath, long? scheduleId = null, string scheduleName = "", string delimiter = ",")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_schedule_csv", new { output_path = outputPath, schedule_id = scheduleId, schedule_name = scheduleName, delimiter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_elements_data", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export element parameter data for a category to a JSON or CSV file. outputPath is an absolute file path. parameterNames defaults to a common set. format: json|csv." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> ExportElementsData(string category, string outputPath, string[] parameterNames = null, string format = "json")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_elements_data", new { category, output_path = outputPath, parameter_names = parameterNames, format });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_batch_export_sheets", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export many sheets at once to PDF or DWG. outputFolder must be an existing absolute path. format: pdf|dwg. sheetIds defaults to ALL sheets; sheetNumberFilter narrows by sheet-number substring. Do not retry on 60s timeout; Revit may still be running the command." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> BatchExportSheets(string outputFolder, string format, long[] sheetIds = null, string sheetNumberFilter = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("batch_export_sheets", new { output_folder = outputFolder, format, sheet_ids = sheetIds, sheet_number_filter = sheetNumberFilter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_export_settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List bounded export settings. kindFilter: all|dwg|print|view_sheet_set; use startIndex + maxResults.")]
        public static async Task<string> ListExportSettings(string kindFilter = "all", int startIndex = 0, int maxResults = 50)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_export_settings", new { kind_filter = kindFilter, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_view_sheet_set", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a named ViewSheetSet (a saved set of views/sheets) for batch printing/exporting. viewIds are the ViewSheet/View ElementIds to include.")]
        public static async Task<string> CreateViewSheetSet(string name, long[] viewIds)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_view_sheet_set", new { name, view_ids = viewIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_print_settings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Report PrintManager state and bounded setting lists. kindFilter: all|print|view_sheet_set; use startIndex + maxResults.")]
        public static async Task<string> GetPrintSettings(string kindFilter = "all", int startIndex = 0, int maxResults = 50)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_print_settings", new { kind_filter = kindFilter, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
