using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("schedule")]
    public class ScheduleTools
    {
        [McpServerTool(Name = "revit_list_schedules", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all schedules in the project. Optional filters: categoryFilter (case-insensitive substring on resolved category name), namePattern (case-insensitive substring on schedule name).")]
        public static async Task<string> ListSchedules(string categoryFilter = "", string namePattern = "")
            => await ToolGateway.CallAsync("list_schedules", new { categoryFilter, namePattern });

        [McpServerTool(Name = "revit_get_schedule_definition", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get the full structural definition of a schedule: fields (parameter/formula/combined), filters, sort/group, and settings. Identify schedule by `scheduleId` (long) or `scheduleName`.")]
        public static async Task<string> GetScheduleDefinition(long? scheduleId = null, string scheduleName = "")
            => await ToolGateway.CallAsync("get_schedule_definition", new { scheduleId, scheduleName });

        [McpServerTool(Name = "revit_get_schedule_data", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get the rendered tabular content of a schedule (header row + body rows) with pagination. Optional cell metadata (cell type + merged cells).")]
        public static async Task<string> GetScheduleData(long? scheduleId = null, string scheduleName = "", int startRow = 0, int maxRows = 200, bool includeCellMeta = false)
            => await ToolGateway.CallAsync("get_schedule_data", new { scheduleId, scheduleName, startRow, maxRows, includeCellMeta });

        [McpServerTool(Name = "revit_get_schedule_formulas", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Extract all calculated (formula) and combined-parameter fields from a schedule, with parsed formula dependencies. Useful for auditing, debugging, or copying formulas between schedules.")]
        public static async Task<string> GetScheduleFormulas(long? scheduleId = null, string scheduleName = "")
            => await ToolGateway.CallAsync("get_schedule_formulas", new { scheduleId, scheduleName });

        [McpServerTool(Name = "revit_get_schedulable_fields", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List parameters that CAN be added as fields to a schedule but have not been added yet. Pre-step for add_schedule_field — call this to discover valid parameter names.")]
        public static async Task<string> GetSchedulableFields(long? scheduleId = null, string scheduleName = "", string[] kindFilter = null)
            => await ToolGateway.CallAsync("get_schedulable_fields", new { scheduleId, scheduleName, kindFilter });

        [McpServerTool(Name = "revit_find_schedule_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Find Revit elements aggregated by a schedule (using FilteredElementCollector scoped to the schedule's id). Returns count grouped by category and per-element {id, name, category, typeName}. Optional includeParameters returns each element's visible parameters with unit-corrected values.")]
        public static async Task<string> FindScheduleElements(long? scheduleId = null, string scheduleName = "", bool groupByCategory = true, bool includeParameters = false, int limit = 500)
            => await ToolGateway.CallAsync("find_schedule_elements", new { scheduleId, scheduleName, groupByCategory, includeParameters, limit });

        [McpServerTool(Name = "revit_create_schedule", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a new schedule from a declarative spec. Supports three field kinds in one transaction: parameter (existing Revit param), formula (calculated value field), and combined (concatenated parameters with separators). Optional filters, sort/group, and isItemized.")]
        public static async Task<string> CreateSchedule(string category, string name,
            [System.ComponentModel.Description("JSON array of field objects, e.g. [{\"parameterName\":\"Number\"},{\"parameterName\":\"Level\"}].")] string fields,
            [System.ComponentModel.Description("JSON array of {fieldName,operator,value}; the field must be in fields. ElementId fields such as Level accept names or numeric IDs.")] string filters = null,
            [System.ComponentModel.Description("JSON array of {fieldName,ascending}; ascending is boolean. order/sortOrder also accept Ascending or Descending; conflicting values are rejected.")] string sortGroup = null,
            bool isItemized = true,
            [System.ComponentModel.Description("Phase name or element ID. Defaults to the active view phase; if unavailable, specify it explicitly.")] string phase = "")
            => await ToolGateway.CallWithParamsAsync("create_schedule", () =>
            {
                var parsedFields = JArray.Parse(fields);
                var parsedFilters = string.IsNullOrWhiteSpace(filters) ? null : JArray.Parse(filters);
                var parsedSortGroup = string.IsNullOrWhiteSpace(sortGroup) ? null : JArray.Parse(sortGroup);
                return new { category, name, fields = parsedFields, filters = parsedFilters, sortGroup = parsedSortGroup, isItemized, phase };
            });

        [McpServerTool(Name = "revit_add_schedule_field", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Add one new field to an existing schedule. Supports parameter, formula, or combined-parameter kinds via a discriminated-union spec. Optional insertIndex, columnHeading, hidden, columnWidth (mm).")]
        public static async Task<string> AddScheduleField(string field, long? scheduleId = null, string scheduleName = "", int? insertIndex = null, string columnHeading = "", bool hidden = false, double? columnWidth = null)
            => await ToolGateway.CallWithParamsAsync("add_schedule_field", () =>
            {
                var parsedField = JObject.Parse(field);
                return new { scheduleId, scheduleName, field = parsedField, insertIndex, columnHeading, hidden, columnWidth };
            });

        [McpServerTool(Name = "revit_update_schedule_field", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Modify existing schedule fields; restore them with Revit Undo. Modify an existing schedule field's properties: columnHeading, hidden, columnWidth, horizontalAlignment, headingOrientation, formula (only if calculated), combinedParameters (only if combined), isTotal, isPercentage, displayType. Cannot change the underlying parameter of a parameter field — use remove + add instead.")]
        public static async Task<string> UpdateScheduleField(
            [System.ComponentModel.Description("JSON object identifying a field, e.g. {\"fieldName\":\"Level\"} or {\"fieldIndex\":3}; not a plain name or number.")] string fieldRef,
            [System.ComponentModel.Description("JSON object of changes, e.g. {\"hidden\":true}.")] string changes,
            long? scheduleId = null, string scheduleName = "")
            => await ToolGateway.CallWithParamsAsync("update_schedule_field", () =>
            {
                var parsedFieldRef = JObject.Parse(fieldRef);
                var parsedChanges = JObject.Parse(changes);
                return new { scheduleId, scheduleName, fieldRef = parsedFieldRef, changes = parsedChanges };
            });

        [McpServerTool(Name = "revit_apply_schedule_filter_sort", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace schedule filters and settings; restore them with Revit Undo. Partially update a schedule's filters, sort/group, and settings. filters/sortGroup replace only when supplied; omitted sections are preserved.")]
        public static async Task<string> ApplyScheduleFilterSort(long? scheduleId = null, string scheduleName = "", string filters = null, string sortGroup = null, string settings = null)
            => await ToolGateway.CallWithParamsAsync("apply_schedule_filter_sort", () =>
            {
                var parsedFilters = string.IsNullOrWhiteSpace(filters) ? null : JArray.Parse(filters);
                var parsedSortGroup = string.IsNullOrWhiteSpace(sortGroup) ? null : JArray.Parse(sortGroup);
                var parsedSettings = string.IsNullOrWhiteSpace(settings) ? null : JObject.Parse(settings);
                return new { scheduleId, scheduleName, filters = parsedFilters, sortGroup = parsedSortGroup, settings = parsedSettings };
            });
    }
}
