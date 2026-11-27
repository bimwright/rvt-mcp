using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using RvtMcp.Server.Prompts;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class WorkflowGuideTools
    {
        [McpServerTool(Name = "revit_get_workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
         Description(
             "Read a bundled Revit workflow guide (or list available guides when name is omitted): " +
             "change (disciplined survey-first edit guide), getting_started, model_audit, pre_issue_check, drawing_layout, or stairs. " +
             "Honors --read-only and active toolsets. Read-only fallback for clients without MCP Skills. " +
             "Example: {name: \"change\"} to read a guide, or {} to list all workflows.")]
        public static string GetWorkflow(
            [Description("Workflow name (change, getting_started, model_audit, pre_issue_check, drawing_layout, stairs), or omit to list all workflows.")]
            string name = null)
        {
            var config = ServerState.Config;
            var enabled = ToolsetFilter.Resolve(config);
            var sessionId = PromptRequestLogging.CurrentSessionId ?? "stdio";
            var callCtx = TargetCallContext.Current;
            var clientName = callCtx?.ClientNameResolved == true ? callCtx.ClientName : ToolGateway.ClientName;

            if (string.IsNullOrWhiteSpace(name))
            {
                PromptRequestLogging.LogWorkflowTool(name, null, sessionId, clientName);
                return JsonConvert.SerializeObject(new
                {
                    workflows = WorkflowCatalog.All.Select(w => new
                    {
                        name = w.Id,
                        prompt = w.PromptName,
                        skill = w.SkillUri,
                        description = w.Description,
                        available = WorkflowCatalog.IsAvailable(w, enabled, config, out _)
                    }).ToArray(),
                    hint = "Call revit_get_workflow(name=\"change\") (or any listed workflow name) to load its full procedure."
                }, Formatting.Indented);
            }

            if (WorkflowCatalog.TryFind(name, out var def))
            {
                PromptRequestLogging.LogWorkflowTool(name, def, sessionId, clientName);
                return WorkflowCatalog.RenderBody(def, enabled, config);
            }

            PromptRequestLogging.LogWorkflowTool(name, null, sessionId, clientName);
            return JsonConvert.SerializeObject(new
            {
                ok = false,
                error = "INVALID_ARGUMENT: unknown workflow name. Call revit_get_workflow() with no arguments to list available workflows.",
                available_workflows = WorkflowCatalog.All.Select(w => w.Id).ToArray()
            }, Formatting.Indented);
        }
    }
}
