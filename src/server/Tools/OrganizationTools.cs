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
    [McpServerToolType, Toolset("organization")]
    public class OrganizationTools
    {
        [McpServerTool(Name = "revit_list_view_templates", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List view templates in the document.")]
        public static async Task<string> ListViewTemplates(string viewType = "", long? viewId = null, bool includeSettings = true, bool includeUsage = false, int limit = 500)
            => await ToolGateway.CallAsync("list_view_templates", new { viewType, viewId, includeSettings, includeUsage, limit });

        [McpServerTool(Name = "revit_create_view_template_from_view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a new view template from an existing view.")]
        public static async Task<string> CreateViewTemplateFromView(string templateName, long? sourceViewId = null, long[] controlledSettingIds = null, long[] nonControlledSettingIds = null, bool failIfNameExists = true)
            => await ToolGateway.CallAsync("create_view_template_from_view", new { templateName, sourceViewId, controlledSettingIds, nonControlledSettingIds, failIfNameExists });

        [McpServerTool(Name = "revit_apply_view_template", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace view settings or template assignments; restore them with Revit Undo. Apply or assign a view template to one or more views.")]
        public static async Task<string> ApplyViewTemplate(long templateId, long[] viewIds = null, string mode = "assign", bool replaceExisting = false)
            => await ToolGateway.CallAsync("apply_view_template", new { templateId, viewIds, mode, replaceExisting });

        [McpServerTool(Name = "revit_duplicate_view_template", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Duplicate an existing view template.")]
        public static async Task<string> DuplicateViewTemplate(long templateId, string newName)
            => await ToolGateway.CallAsync("duplicate_view_template", new { templateId, newName });

        [McpServerTool(Name = "revit_delete_view_template", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Delete a view template and optionally detach dependent views; restore them with Revit Undo. Delete a view template. maxUsedByViews caps dependent-view/deleted-id response previews.")]
        public static async Task<string> DeleteViewTemplate(long templateId, bool dryRun = true, bool clearFromViews = false, int maxUsedByViews = 100)
            => await ToolGateway.CallAsync("delete_view_template", new { templateId, dryRun, clearFromViews, max_used_by_views = maxUsedByViews });

        [McpServerTool(Name = "revit_save_selection", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create or replace a saved selection; restore document changes with Revit Undo. Save a named selection. IDs are omitted from the response by default; opt in with includeElementIds and maxElementIdResults.")]
        public static async Task<string> SaveSelection(string name, long[] elementIds = null, bool replaceExisting = false, bool useActiveSelectionIfIdsOmitted = true, bool includeElementIds = false, int maxElementIdResults = 100)
            => await ToolGateway.CallAsync("save_selection", new { name, elementIds, replaceExisting, useActiveSelectionIfIdsOmitted, include_element_ids = includeElementIds, max_element_id_results = maxElementIdResults });

        [McpServerTool(Name = "revit_load_selection", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Load a bounded page from a saved selection filter. Use startIndex + maxResults (hard max 1000).")]
        public static async Task<string> LoadSelection(string name = "", long? selectionId = null, bool includeElementSummary = false, int startIndex = 0, int maxResults = 200)
            => await ToolGateway.CallAsync("load_selection", new { name, selectionId, includeElementSummary, start_index = startIndex, max_results = maxResults });

        [McpServerTool(Name = "revit_list_saved_selections", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List all saved selection filters in the document.")]
        public static async Task<string> ListSavedSelections(string nameFilter = "", bool includeElementIds = false, bool includeElementSummary = false, int limit = 500)
            => await ToolGateway.CallAsync("list_saved_selections", new { nameFilter, includeElementIds, includeElementSummary, limit });

        [McpServerTool(Name = "revit_delete_saved_selection", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Delete a saved selection filter; restore it with Revit Undo. Delete a saved selection filter by name or ID.")]
        public static async Task<string> DeleteSavedSelection(string name = "", long? selectionId = null, bool dryRun = true)
            => await ToolGateway.CallAsync("delete_saved_selection", new { name, selectionId, dryRun });

        [McpServerTool(Name = "revit_select_elements", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Update active selection in the Revit UI. Runs pure UI selection mutation (must NOT run in transaction).")]
        public static async Task<string> SelectElements(long[] elementIds = null, string savedSelectionName = "", long? savedSelectionId = null, bool zoomToSelection = false)
            => await ToolGateway.CallAsync("select_elements", new { elementIds, savedSelectionName, savedSelectionId, zoomToSelection });
    }
}
