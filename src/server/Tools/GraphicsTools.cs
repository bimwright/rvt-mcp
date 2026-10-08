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
    [McpServerToolType, Toolset("graphics")]
    public class GraphicsTools
    {
        [McpServerTool(Name = "revit_create_view_filter", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a parameter-based view filter (ParameterFilterElement) targeting one or more categories. rules is an optional JSON array of {parameter_name, evaluator, value} where evaluator is equals|not_equals|greater|less|contains|begins_with|ends_with. Omit rules for a category-only filter.")]
        public static async Task<string> CreateViewFilter(string name, string[] categories, string rules = "")
        {
            try
            {
                var parsedRules = string.IsNullOrWhiteSpace(rules) ? null : JArray.Parse(rules);
                var result = await ToolGateway.SendToRevit("create_view_filter", new { name, categories, rules = parsedRules });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_apply_filter_to_view", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change view filter visibility; restore it with Revit Undo. Add an existing view filter (ParameterFilterElement) to a view's filter list. viewId defaults to the active view. visible sets the initial visibility of matching elements.")]
        public static async Task<string> ApplyFilterToView(long filterId, long? viewId = null, bool visible = true)
            => await ToolGateway.CallAsync("apply_filter_to_view", new { filter_id = filterId, view_id = viewId, visible });

        [McpServerTool(Name = "revit_set_filter_overrides", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace view filter graphics; restore them with Revit Undo. Set graphic overrides for a filter already applied to a view. Colors are hex '#RRGGBB'. transparency 0-100, projectionLineWeight 1-16. Only supplied properties change; others are preserved. viewId defaults to active view.")]
        public static async Task<string> SetFilterOverrides(long filterId, long? viewId = null, string projectionLineColor = "", string surfaceForegroundColor = "", string cutLineColor = "", int? transparency = null, bool? halftone = null, int? projectionLineWeight = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_filter_overrides", new
                {
                    filter_id = filterId,
                    view_id = viewId,
                    projection_line_color = string.IsNullOrEmpty(projectionLineColor) ? null : projectionLineColor,
                    surface_foreground_color = string.IsNullOrEmpty(surfaceForegroundColor) ? null : surfaceForegroundColor,
                    cut_line_color = string.IsNullOrEmpty(cutLineColor) ? null : cutLineColor,
                    transparency,
                    halftone,
                    projection_line_weight = projectionLineWeight
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_view_filters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all view filter definitions (ParameterFilterElement) in the document. If viewId is supplied, only filters applied to that view. includeUsage lists which views each filter is applied to.")]
        public static async Task<string> ListViewFilters(long? viewId = null, bool includeUsage = false)
            => await ToolGateway.CallAsync("list_view_filters", new { view_id = viewId, include_usage = includeUsage });

        [McpServerTool(Name = "revit_remove_filter_from_view", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Remove a view filter and optionally delete its definition; restore it with Revit Undo. Remove a view filter from a view's filter list. viewId defaults to active view. deleteDefinitionIfUnused deletes the ParameterFilterElement entirely if no other view uses it.")]
        public static async Task<string> RemoveFilterFromView(long filterId, long? viewId = null, bool deleteDefinitionIfUnused = false)
            => await ToolGateway.CallAsync("remove_filter_from_view", new { filter_id = filterId, view_id = viewId, delete_definition_if_unused = deleteDefinitionIfUnused });

        [McpServerTool(Name = "revit_override_element_graphics", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace element graphics; restore them with Revit Undo. Apply per-element view-specific graphic overrides (color, transparency, halftone, line weight) to elements in a view. Colors are hex '#RRGGBB'. transparency 0-100, projectionLineWeight 1-16. viewId defaults to active view.")]
        public static async Task<string> OverrideElementGraphics(long[] elementIds, long? viewId = null, string projectionLineColor = "", string surfaceForegroundColor = "", string cutLineColor = "", int? transparency = null, bool? halftone = null, int? projectionLineWeight = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("override_element_graphics", new
                {
                    element_ids = elementIds,
                    view_id = viewId,
                    projection_line_color = string.IsNullOrEmpty(projectionLineColor) ? null : projectionLineColor,
                    surface_foreground_color = string.IsNullOrEmpty(surfaceForegroundColor) ? null : surfaceForegroundColor,
                    cut_line_color = string.IsNullOrEmpty(cutLineColor) ? null : cutLineColor,
                    transparency,
                    halftone,
                    projection_line_weight = projectionLineWeight
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_clear_element_overrides", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Clear saved element graphics; restore them with Revit Undo. Reset per-element view-specific graphic overrides to default. If elementIds is omitted, clears overrides on every element in the view that currently has them. viewId defaults to active view.")]
        public static async Task<string> ClearElementOverrides(long[] elementIds = null, long? viewId = null)
            => await ToolGateway.CallAsync("clear_element_overrides", new { element_ids = elementIds, view_id = viewId });

        [McpServerTool(Name = "revit_get_view_visibility", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Report a view's visibility/graphics state: hidden categories, applied filters, detail level, discipline, scale, view template, graphics-overrides-allowed. includeCategoryList lists every model category with its hidden state. viewId defaults to active view.")]
        public static async Task<string> GetViewVisibility(long? viewId = null, bool includeCategoryList = false)
            => await ToolGateway.CallAsync("get_view_visibility", new { view_id = viewId, include_category_list = includeCategoryList });

        [McpServerTool(Name = "revit_set_category_visibility", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Change saved category visibility; restore it with Revit Undo. Show or hide model categories in a view. categories is an array of category names. hidden=true hides, false shows. viewId defaults to active view.")]
        public static async Task<string> SetCategoryVisibility(string[] categories, bool hidden, long? viewId = null)
            => await ToolGateway.CallAsync("set_category_visibility", new { categories, hidden, view_id = viewId });

        [McpServerTool(Name = "revit_list_phases", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all project phases (in sequence order) and all phase filters. Call before set_view_phase or set_element_phase to discover valid phase names/ids.")]
        public static async Task<string> ListPhases()
            => await ToolGateway.CallAsync("list_phases", new { });

        [McpServerTool(Name = "revit_set_view_phase", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace saved view phase settings; restore them with Revit Undo. Set a view's Phase and/or Phase Filter. Identify each by id or name. At least one of phase / phase filter must be supplied. viewId defaults to active view.")]
        public static async Task<string> SetViewPhase(long? viewId = null, long? phaseId = null, string phaseName = "", long? phaseFilterId = null, string phaseFilterName = "")
            => await ToolGateway.CallAsync("set_view_phase", new { view_id = viewId, phase_id = phaseId, phase_name = phaseName, phase_filter_id = phaseFilterId, phase_filter_name = phaseFilterName });

        [McpServerTool(Name = "revit_set_element_phase", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace element phase assignments; restore them with Revit Undo. Set the Phase Created and/or Phase Demolished of elements. Identify phases by id or name. Use phaseDemolishedName='None' to clear demolition. At least one phase must be supplied.")]
        public static async Task<string> SetElementPhase(long[] elementIds, long? phaseCreatedId = null, string phaseCreatedName = "", long? phaseDemolishedId = null, string phaseDemolishedName = "")
            => await ToolGateway.CallAsync("set_element_phase", new { element_ids = elementIds, phase_created_id = phaseCreatedId, phase_created_name = phaseCreatedName, phase_demolished_id = phaseDemolishedId, phase_demolished_name = phaseDemolishedName });
    }
}
