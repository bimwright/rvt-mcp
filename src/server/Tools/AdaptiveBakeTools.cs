using System;
using System.Threading.Tasks;
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("toolbaker")]
    public class AdaptiveBakeTools
    {
        internal static Func<BakeDb> AcceptStoreFactory = () => new BakeDb(new BakePaths());
        [McpServerTool(Name = "revit_list_bake_suggestions", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "List bounded adaptive ToolBaker suggestions. Filter by state and page with startIndex + limit (hard max 500).")]
        public static string ListBakeSuggestions(string state = "", int startIndex = 0, int limit = 100)
        {
            try
            {
                var paths = new BakePaths();
                using var db = new BakeDb(paths);
                db.Migrate();
                return ListBakeSuggestionsHandler.Handle(db, ToolGateway.UsageLogger, state, startIndex, limit);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_accept_bake_suggestion", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Accept an adaptive ToolBaker suggestion by id. Validates name, schema, and output choice, then prepares a bake request without native tool promotion." +
            " Updates stored bake state and can replace a baked tool. Revit Undo cannot restore bake files; restore prior source or a backup.")]
        public static async Task<string> AcceptBakeSuggestion(string id, string name, string output_choice = "mcp_only", string params_schema = null)
        {
            try
            {
                using var db = AcceptStoreFactory();
                db.Migrate();
                Newtonsoft.Json.Linq.JObject applied = null;
                var json = await AcceptBakeSuggestionHandler.HandleAsync(
                    db,
                    id,
                    name,
                    output_choice,
                    params_schema,
                    pluginApply: async request => applied = await ToolGateway.SendToRevit("apply_bake", request));
                return ToolGateway.CarryOnce(applied, json);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_dismiss_bake_suggestion", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Dismiss an adaptive ToolBaker suggestion. action must be snooze_30d, never, or never_with_gap_signal." +
            " Changes persistent suggestion state. Revit Undo does not revert it; restore a bake database backup if needed.")]
        public static string DismissBakeSuggestion(string id, string action)
        {
            try
            {
                var paths = new BakePaths();
                using var db = new BakeDb(paths);
                db.Migrate();
                var revitVersion = ToolGateway.CurrentRevitVersion ?? "unknown";
                return DismissBakeSuggestionHandler.Handle(
                    db,
                    id,
                    action,
                    currentRevitVersion: revitVersion,
                    auditLog: new ToolBakerAuditLog(paths.AuditJsonl));
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
