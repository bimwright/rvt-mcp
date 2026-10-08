using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("sheets")]
    public class SheetsTools
    {
        private static JArray NormalizeOptionalJsonArray(object value, string parameterName)
        {
            if (value == null)
                return null;

            if (value is JArray jArray)
                return jArray;

            if (value is JToken token)
            {
                if (token.Type == JTokenType.Null)
                    return null;
                if (token.Type == JTokenType.Array)
                    return (JArray)token;
                throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
                    return null;
                if (element.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
                return JArray.Parse(element.GetRawText());
            }

            if (value is string)
                throw new ArgumentException($"{parameterName} must be a JSON array, not a string.");

            try
            {
                var tokenFromObject = McpJsonInput.FromObject(value);
                if (tokenFromObject.Type == JTokenType.Null)
                    return null;
                if (tokenFromObject.Type == JTokenType.Array)
                    return (JArray)tokenFromObject;
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"{parameterName} could not be converted to a JSON array: {ex.Message}");
            }

            throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
        }

        [McpServerTool(Name = "revit_create_sheet", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a new sheet with a titleblock")]
        public static async Task<string> CreateSheet(string sheetNumber, string sheetName, long? titleBlockTypeId = null, string titleBlockName = "")
            => await ToolGateway.CallAsync("create_sheet", new
            {
                sheet_number = sheetNumber,
                sheet_name = sheetName,
                title_block_type_id = titleBlockTypeId,
                title_block_name = titleBlockName
            });

        [McpServerTool(Name = "revit_duplicate_sheet", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Duplicate an existing sheet with viewport and schedule layout")]
        public static async Task<string> DuplicateSheet(string newSheetNumber, long? sourceSheetId = null, string sourceSheetNumber = "", string newSheetName = "", string duplicateViewOption = "with_detailing", bool includeSchedules = true, bool includeRevisions = true, bool reuseViewsWhenAllowed = true)
            => await ToolGateway.CallAsync("duplicate_sheet", new
            {
                source_sheet_id = sourceSheetId,
                source_sheet_number = sourceSheetNumber,
                new_sheet_number = newSheetNumber,
                new_sheet_name = newSheetName,
                duplicate_view_option = duplicateViewOption,
                include_schedules = includeSchedules,
                include_revisions = includeRevisions,
                reuse_views_when_allowed = reuseViewsWhenAllowed
            });

        [McpServerTool(Name = "revit_create_placeholder_sheet", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a new placeholder sheet")]
        public static async Task<string> CreatePlaceholderSheet(string sheetNumber, string sheetName)
            => await ToolGateway.CallAsync("create_placeholder_sheet", new
            {
                sheet_number = sheetNumber,
                sheet_name = sheetName
            });

        [McpServerTool(Name = "revit_list_sheets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List sheets matching filters, with viewport and schedule counts, title blocks, and revisions")]
        public static async Task<string> ListSheets(string numberFilter = "", string namePattern = "", bool includeRevisions = true, bool includeViewports = false, bool includePlaceholders = true, int limit = 1000)
            => await ToolGateway.CallAsync("list_sheets", new
            {
                number_filter = numberFilter,
                name_pattern = namePattern,
                include_revisions = includeRevisions,
                include_viewports = includeViewports,
                include_placeholders = includePlaceholders,
                limit
            });

        [McpServerTool(Name = "revit_set_titleblock_parameters", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace titleblock parameter values; restore them with Revit Undo. Set titleblock instance and type parameters for a sheet. parameters: object map of {paramName: value}.")]
        public static async Task<string> SetTitleblockParameters(IDictionary<string, object> parameters, long? sheetId = null, string sheetNumber = "", string target = "instance")
            => await ToolGateway.CallWithParamsAsync("set_titleblock_parameters", () =>
            {
                var parsedParameters = ToolbakerTools.NormalizeRunBakedToolParams(parameters);
                return new
                {
                    sheet_id = sheetId,
                    sheet_number = sheetNumber,
                    target,
                    parameters = parsedParameters
                };
            });

        [McpServerTool(Name = "revit_get_titleblock_parameters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get titleblock instance and type parameters for a sheet")]
        public static async Task<string> GetTitleblockParameters(long? sheetId = null, string sheetNumber = "", string target = "both", bool includeReadOnly = true)
            => await ToolGateway.CallAsync("get_titleblock_parameters", new
            {
                sheet_id = sheetId,
                sheet_number = sheetNumber,
                target,
                include_read_only = includeReadOnly
            });

        [McpServerTool(Name = "revit_list_titleblocks", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List loaded titleblock family types and count their sheet placements")]
        public static async Task<string> ListTitleblocks(string namePattern = "", bool includeInactive = true, int limit = 1000)
            => await ToolGateway.CallAsync("list_titleblocks", new
            {
                name_pattern = namePattern,
                include_inactive = includeInactive,
                limit
            });

        [McpServerTool(Name = "revit_place_schedule_on_sheet", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Place a schedule on a sheet using sheet paper coordinates in millimeters")]
        public static async Task<string> PlaceScheduleOnSheet(double xMm, double yMm, long? sheetId = null, string sheetNumber = "", long? scheduleId = null, string scheduleName = "")
            => await ToolGateway.CallAsync("place_schedule_on_sheet", new
            {
                sheet_id = sheetId,
                sheet_number = sheetNumber,
                schedule_id = scheduleId,
                schedule_name = scheduleName,
                x_mm = xMm,
                y_mm = yMm
            });

        [McpServerTool(Name = "revit_create_revision", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a new document revision")]
        public static async Task<string> CreateRevision(string description, string date = "", string issuedTo = "", string issuedBy = "", bool issued = false)
            => await ToolGateway.CallAsync("create_revision", new
            {
                description,
                date,
                issued_to = issuedTo,
                issued_by = issuedBy,
                issued
            });

        [McpServerTool(Name = "revit_assign_revision_to_sheet", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace sheet revision assignments; restore them with Revit Undo. Assign or remove a revision on sheets")]
        public static async Task<string> AssignRevisionToSheet(long revisionId, long[] sheetIds = null, string[] sheetNumbers = null, string mode = "append")
            => await ToolGateway.CallAsync("assign_revision_to_sheet", new
            {
                revision_id = revisionId,
                sheet_ids = sheetIds,
                sheet_numbers = sheetNumbers,
                mode
            });

        [McpServerTool(Name = "revit_list_revisions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List project revisions and optionally their assigned sheets")]
        public static async Task<string> ListRevisions(bool includeSheets = true)
            => await ToolGateway.CallAsync("list_revisions", new
            {
                include_sheets = includeSheets
            });

        [McpServerTool(Name = "revit_renumber_sheets", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace sheet numbers or names; restore them with Revit Undo. Bulk renumber/rename sheets with collision preflights and cyclic swap support")]
        public static async Task<string> RenumberSheets(object items = null, string find = "", string replace = "", string prefix = "", string suffix = "", bool dryRun = true)
            => await ToolGateway.CallWithParamsAsync("renumber_sheets", () =>
            {
                var normalizedItems = NormalizeOptionalJsonArray(items, nameof(items));
                return new
                {
                    items = normalizedItems,
                    find,
                    replace,
                    prefix,
                    suffix,
                    dry_run = dryRun
                };
            });

        [McpServerTool(Name = "revit_get_viewport_geometry", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read viewport positions on a sheet in sheet-space millimetres: visible crop rectangle, view outline, scale, rotation, pinned and crop flags. Sheet coordinates are paper millimetres, not project coordinates. Use startViewport + maxViewports (hard max 500).")]
        public static async Task<string> GetViewportGeometry(long? sheetId = null, string sheetNumber = "", long[] viewportIds = null, int startViewport = 0, int maxViewports = 100)
            => await ToolGateway.CallAsync("get_viewport_geometry", new
            {
                sheet_id = sheetId,
                sheet_number = sheetNumber,
                viewport_ids = viewportIds,
                start_viewport = startViewport,
                max_viewports = maxViewports
            });

        [McpServerTool(Name = "revit_align_viewports", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace viewport positions on a sheet; restore them with Revit Undo. Align viewports to a reference viewport (left, right, top, bottom, center_x, center_y, center) or centre them on the sheet (center_on_sheet) using sheet-space millimetres of the visible crop rectangle. dryRun defaults to true and returns the planned moves without changing anything.")]
        public static async Task<string> AlignViewports(string mode, long? sheetId = null, string sheetNumber = "", long? referenceViewportId = null, long[] viewportIds = null, bool dryRun = true)
            => await ToolGateway.CallAsync("align_viewports", new
            {
                mode,
                sheet_id = sheetId,
                sheet_number = sheetNumber,
                reference_viewport_id = referenceViewportId,
                viewport_ids = viewportIds,
                dry_run = dryRun
            });
    }
}
