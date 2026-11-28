using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RvtMcp.Server.Prompts
{
    /// <summary>
    /// MCP prompt surface (spec D1–D4). Thin user shortcuts delegating to the
    /// shared <see cref="WorkflowCatalog"/> and <see cref="PromptBody"/> renderer,
    /// which swaps in an enable notice when required toolsets (or send_code) are hidden.
    /// </summary>
    [McpServerPromptType]
    public class RevitPrompts
    {
        [McpServerPrompt(Name = "revit_getting_started"),
         Description(WorkflowCatalog.GettingStartedDescription)]
        public static string GettingStarted()
            => WorkflowCatalog.RenderForPrompt("getting_started");

        [McpServerPrompt(Name = "revit_change"),
         Description(WorkflowCatalog.ChangeDescription)]
        public static string Change(
            [Description("The requested model change; include target elements and the reason when known")] string change)
            => WorkflowCatalog.RenderForPrompt("change", change);

        [McpServerPrompt(Name = "revit_model_audit"),
         Description(WorkflowCatalog.ModelAuditDescription)]
        public static string ModelAudit(
            [Description("Part of the model to audit; 'all' = whole model")] string scope = "all")
            => WorkflowCatalog.RenderForPrompt("model_audit", scope);

        [McpServerPrompt(Name = "revit_pre_issue_check"),
         Description(WorkflowCatalog.PreIssueCheckDescription)]
        public static string PreIssueCheck(
            [Description("Sheet numbers/IDs, an explicit number/name filter, or 'all'; named sheet sets require their member sheets")] string scope = "all")
            => WorkflowCatalog.RenderForPrompt("pre_issue_check", scope);

        [McpServerPrompt(Name = "revit_drawing_layout"),
         Description(WorkflowCatalog.DrawingLayoutDescription)]
        public static string DrawingLayout(
            [Description("The layout change wanted: sheet number/ID, which viewports, and how to align or centre them")] string request)
            => WorkflowCatalog.RenderForPrompt("drawing_layout", request);

        [McpServerPrompt(Name = "revit_stairs"),
         Description(WorkflowCatalog.StairsDescription)]
        public static string Stairs()
            => WorkflowCatalog.RenderForPrompt("stairs");
    }
}
