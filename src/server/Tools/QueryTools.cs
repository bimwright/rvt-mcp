using System;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{

    [McpServerToolType, Toolset("query")]
    public class QueryTools
    {
        [McpServerTool(Name = "revit_get_current_view_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get active view info. Returns viewName, viewType (FloorPlan/Section/3D/Sheet), level, scale, detailLevel, displayStyle. Call before creating elements to know active level.")]
        public static async Task<string> GetCurrentViewInfo()
            => await ToolGateway.CallAsync("get_current_view_info");

        [McpServerTool(Name = "revit_get_selected_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get currently selected Revit elements with paging. Use startIndex + maxResults (default 200, hard max 1000).")]
        public static async Task<string> GetSelectedElements(int startIndex = 0, int maxResults = 200)
            => await ToolGateway.CallAsync("get_selected_elements", new { start_index = startIndex, max_results = maxResults });

        [McpServerTool(Name = "revit_get_available_family_types", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List loadable family types. Returns {familyName, typeName, typeId} grouped by category. Optional: filter by category (e.g. 'Walls', 'Doors', 'Pipes' — NOT 'OST_Walls'). Feed typeId into create_point_based_element.")]
        public static async Task<string> GetAvailableFamilyTypes(string category = "")
            => await ToolGateway.CallAsync("get_available_family_types", new { category });

        [McpServerTool(Name = "revit_ai_element_filter", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Filter elements by category + parameter. Numeric values in mm (auto-converted). category uses human name ('Pipes', NOT 'OST_Pipes'). Operators: equals/contains/startswith/greaterthan/lessthan. select=true highlights results. Example: category='Pipes', parameterName='Diameter', parameterValue='200', operator='greaterthan', select=true.")]
        public static async Task<string> AiElementFilter(string category, string parameterName = "", string parameterValue = "", string @operator = "equals", int limit = 100, bool select = false)
            => await ToolGateway.CallAsync("ai_element_filter", new { category, parameterName, parameterValue, @operator, limit, select });

        [McpServerTool(Name = "revit_analyze_model_statistics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Count elements grouped by category (Walls, Doors, Pipes, etc.). Call to understand project scope before detailed queries. Iteration is capped at maxElements (default 100000); raise it for full accuracy on very large models at the cost of a longer UI freeze.")]
        public static async Task<string> AnalyzeModelStatistics(int? maxElements = null)
            => await ToolGateway.CallAsync("analyze_model_statistics", new { maxElements });

        [McpServerTool(Name = "revit_get_material_quantities", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Sum material quantities (area m², volume m³) by category. Narrow with materialNameFilter and page with startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetMaterialQuantities(string category, string materialNameFilter = "", int startIndex = 0, int maxResults = 200)
            => await ToolGateway.CallAsync("get_material_quantities", new { category, material_name_filter = materialNameFilter, start_index = startIndex, max_results = maxResults });

        [McpServerTool(Name = "revit_get_element_details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read detailed metadata for one or more elements. Returns identity, category, type, level, workset, phase, owner view, design option, group/assembly ids, location, and bounding box in mm.")]
        public static async Task<string> GetElementDetails(long[] elementIds)
            => await ToolGateway.CallAsync("get_element_details", new { elementIds });

        [McpServerTool(Name = "revit_get_element_parameters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read instance parameters for one or more elements. Returns storage type, read-only state, display value, raw value, and data/spec ids.")]
        public static async Task<string> GetElementParameters(long[] elementIds, bool includeReadOnly = true)
            => await ToolGateway.CallAsync("get_element_parameters", new { elementIds, includeReadOnly });

        [McpServerTool(Name = "revit_get_type_parameters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read type parameters from explicit type ids or from the types of provided element ids.")]
        public static async Task<string> GetTypeParameters(long[] elementIds = null, long[] typeIds = null)
            => await ToolGateway.CallAsync("get_type_parameters", new { elementIds, typeIds });

        [McpServerTool(Name = "revit_list_project_parameters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List project/shared parameter bindings, including instance/type binding kind and bound categories.")]
        public static async Task<string> ListProjectParameters(bool includeCategories = true)
            => await ToolGateway.CallAsync("list_project_parameters", new { includeCategories });

        [McpServerTool(Name = "revit_get_element_relationships", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read host, group, assembly, owner view, design option, family nesting, and dependent-element relationships for elements.")]
        public static async Task<string> GetElementRelationships(long[] elementIds, bool includeDependents = true)
            => await ToolGateway.CallAsync("get_element_relationships", new { elementIds, includeDependents });

        [McpServerTool(Name = "revit_survey_change_impact", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
        [System.ComponentModel.Description("Survey ten relationship groups for explicit active-document targets before proposing a change. Returns found/none/not_checked, coverage, exact/lower-bound counts and stopFlags; never an authorization to write. Linked contents and engineering correctness are not covered. Views/schedules are not scanned unless maxViews > 0; native view calls can exceed the soft time budget. Observational parameters are not trusted history baselines.")]
        public static async Task<string> SurveyChangeImpact(
            [System.ComponentModel.Description("1-25 active-document element IDs; never an implicit whole-model scope.")] long[] elementIds,
            [System.ComponentModel.Description("Required scope threshold agreed for this request, 1-200000. Counts target/propagation candidates, not predicted modifications. No fixed default.")] int scopeThreshold,
            [System.ComponentModel.Description("instance|type|unknown. Type means editing a type definition; assigning a different type to instances is instance.")] string changeKind = "unknown",
            [System.ComponentModel.Description("1 or 2. Depth2 expands type/group/join/physical-connector neighbors only.")] int depth = 1,
            [System.ComponentModel.Description("sheets|all; applies only when maxViews > 0.")] string viewScope = "sheets",
            [System.ComponentModel.Description("Optional explicit phase ID (0 can be valid); otherwise use a valid active-view phase, never the last project phase.")] long? phaseId = null,
            [System.ComponentModel.Description("Optional IDs of up to 100 views within viewScope. Does not implicitly enable view scanning.")] long[] viewIds = null,
            [System.ComponentModel.Description("Bounding-box candidate padding in millimeters, 0-10000; this is not clash detection.")] double proximityPaddingMm = 100,
            [System.ComponentModel.Description("Returned ID/evidence preview cap per group, 1-200; counts remain independent of this cap.")] int maxIdsPerRelation = 100,
            [System.ComponentModel.Description("Soft elapsed-time budget, 1-30000 ms. Stops between native API calls; cannot preempt a native call.")] int budgetMs = 10000,
            [System.ComponentModel.Description("0-100. Default 0 skips view/schedule iteration. Set a positive value only for an explicitly scoped presentation survey.")] int maxViews = 0,
            [System.ComponentModel.Description("Shared scan-work cap, 1-200000; exhausted coverage remains partial.")] int maxScannedElements = 50000,
            [System.ComponentModel.Description("Distinct discovered-node cap per relation and propagation scope, 1-20000.")] int maxGraphNodes = 10000)
        {
            try
            {
                var request = new JObject
                {
                    ["elementIds"] = elementIds == null ? JValue.CreateNull() : new JArray(elementIds),
                    ["scopeThreshold"] = scopeThreshold, ["changeKind"] = changeKind, ["depth"] = depth,
                    ["viewScope"] = viewScope, ["proximityPaddingMm"] = proximityPaddingMm,
                    ["maxIdsPerRelation"] = maxIdsPerRelation, ["budgetMs"] = budgetMs,
                    ["maxViews"] = maxViews, ["maxScannedElements"] = maxScannedElements, ["maxGraphNodes"] = maxGraphNodes
                };
                if (phaseId.HasValue) request["phaseId"] = phaseId.Value;
                if (viewIds != null) request["viewIds"] = new JArray(viewIds);
                RvtMcp.Plugin.Survey.SurveyOptions.Parse(request);
                var result = await ToolGateway.SendToRevit("survey_change_impact", request);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_groups", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List model/detail/attached groups with type, owner view, parent, and optional member ids.")]
        public static async Task<string> ListGroups(string groupKind = "all", bool includeMembers = false)
            => await ToolGateway.CallAsync("list_groups", new { groupKind, includeMembers });

        [McpServerTool(Name = "revit_get_group_members", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read a group instance and a page of member elements. Use startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetGroupMembers(long groupId, int startIndex = 0, int maxResults = 200)
            => await ToolGateway.CallAsync("get_group_members", new { groupId, start_index = startIndex, max_results = maxResults });

        [McpServerTool(Name = "revit_list_assemblies", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List a page of assembly instances. maxMembersPerAssembly caps optional member-id previews.")]
        public static async Task<string> ListAssemblies(bool includeMembers = false, int startIndex = 0, int maxResults = 100, int maxMembersPerAssembly = 50)
            => await ToolGateway.CallAsync("list_assemblies", new { includeMembers, start_index = startIndex, max_results = maxResults, max_members_per_assembly = maxMembersPerAssembly });

        [McpServerTool(Name = "revit_get_assembly_members", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read an assembly instance and a page of members. Use startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetAssemblyMembers(long assemblyId, int startIndex = 0, int maxResults = 200)
            => await ToolGateway.CallAsync("get_assembly_members", new { assemblyId, start_index = startIndex, max_results = maxResults });

        [McpServerTool(Name = "revit_list_worksets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List document worksets and active workset. Optionally includes per-workset element counts.")]
        public static async Task<string> ListWorksets(bool includeElementCounts = false)
            => await ToolGateway.CallAsync("list_worksets", new { includeElementCounts });
    }
}
