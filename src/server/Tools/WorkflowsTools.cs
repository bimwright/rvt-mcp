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
    [McpServerToolType, Toolset("workflows")]
    public class WorkflowsTools
    {
        [McpServerTool(Name = "revit_workflow_clash_review", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create clash-review views and overwrite graphics; restore model changes with Revit Undo. Run clash detection, optionally create a review view, color clash hits, and add review markers with an auditable workflow report.")]
        public static async Task<string> WorkflowClashReview(string category_a, string category_b, long? view_id = null, int max_pairs = 200, bool create_review_view = true, bool color_hits = true, bool create_markers = false, bool dry_run = true, bool continue_on_error = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_clash_review", new { category_a, category_b, view_id, max_pairs, create_review_view, color_hits, create_markers, dry_run, continue_on_error });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_model_audit", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Run a read-only composite model audit covering warnings, families, views, schedules, and MEP connectivity signals.")]
        public static async Task<string> WorkflowModelAudit(bool include_warnings = true, bool include_families = true, bool include_views = true, bool include_schedules = true, bool include_mep = true, int limit_per_section = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_model_audit", new { include_warnings, include_families, include_views, include_schedules, include_mep, limit_per_section });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_room_documentation", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Generate room documentation records, optional callouts, room tags, finish schedule, and sheet placement.")]
        public static async Task<string> WorkflowRoomDocumentation(long[] room_ids = null, string level_name = "", bool create_callouts = true, bool create_finish_schedule = true, bool tag_rooms = true, long? sheet_id = null, bool dry_run = true, int limit = 50)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_room_documentation", new { room_ids, level_name, create_callouts, create_finish_schedule, tag_rooms, sheet_id, dry_run, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_sheet_set", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create sheets and replace sheet data; restore model changes with Revit Undo. Create a coordinated sheet set, place views and schedules, and set sheet parameters with dry-run and rollback reporting.")]
        public static async Task<string> WorkflowSheetSet(System.Collections.Generic.List<object> sheets, string renumber_strategy = "none", bool dry_run = true, bool continue_on_error = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_sheet_set", new { sheets, renumber_strategy, dry_run, continue_on_error });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_data_roundtrip", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Export/import category parameter data with validation. output=file writes the response report as NDJSON to a local same-machine absolute path with bounded preview; remote clients receive preview only." +
            " May overwrite existing output files. Revit Undo cannot restore them; back up the destination first.")]
        public static async Task<string> WorkflowDataRoundtrip(string category, string export_path, string import_path = "", string mode = "export_only", bool dry_run = true, string key_field = "element_id", string[] parameter_names = null, string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_data_roundtrip", new { category, export_path, import_path, mode, dry_run, key_field, parameter_names, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_view_cleanup", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Delete eligible views or schedules; restore them with Revit Undo. Analyze unused views, empty schedules, and naming outliers, with guarded optional deletion of safe candidates.")]
        public static async Task<string> WorkflowViewCleanup(bool include_unused_views = true, bool include_empty_schedules = true, bool include_naming_outliers = true, bool delete_empty_views = false, bool dry_run = true, int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_view_cleanup", new { include_unused_views, include_empty_schedules, include_naming_outliers, delete_empty_views, dry_run, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_naming_normalization", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Rename existing model elements; restore their names with Revit Undo. Analyze and optionally rename views, sheets, levels, and grids using deterministic normalization or a token pattern.")]
        public static async Task<string> WorkflowNamingNormalization(string target, string profile = "", string pattern = "", long[] ids = null, bool dry_run = true, int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_naming_normalization", new { target, profile, pattern, ids, dry_run, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_takeoff_report", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Generate a takeoff report and optionally overwrite a JSON/CSV file via output_path; Revit Undo cannot restore files, so back up the destination. Generate category, quantity, material, and optional cost takeoff reports. output=file writes relational SQLite to a local same-machine absolute path with schema and bounded preview; remote clients receive preview only.")]
        public static async Task<string> WorkflowTakeoffReport(string[] categories = null, bool include_materials = true, bool include_quantities = true, bool include_cost = false, string output_path = "", int limit_per_category = 500, string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_takeoff_report", new { categories, include_materials, include_quantities, include_cost, output_path, limit_per_category, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
