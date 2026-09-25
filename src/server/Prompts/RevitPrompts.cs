using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RvtMcp.Server.Prompts
{
    /// <summary>
    /// MCP prompt surface (spec D1–D4). Always registered — prompts don't consume model
    /// context until the user invokes one. Each method renders its embedded markdown
    /// through <see cref="PromptBody"/>, which swaps in an enable notice when the
    /// prompt's toolsets (or send_code) aren't exposed on this server.
    /// </summary>
    [McpServerPromptType]
    public class RevitPrompts
    {
        private static readonly string[] GettingStartedSets = { "query", "meta" };
        private static readonly string[] ModelAuditSets = { "workflows", "families", "lint", "meta" };
        private static readonly string[] PreIssueSets = { "sheets", "view", "annotation", "lint", "meta" };
        // Spec §3: stairs needs meta (revit_get_current_target) even though send_code
        // can also ride the toolbaker set — without the gate a toolbaker-only server
        // would render a body whose first step isn't exposed.
        private static readonly string[] StairsSets = { "meta" };

        [McpServerPrompt(Name = "revit_getting_started"),
         Description("Read-only orientation for the connected Revit: current target, active view, model statistics. Safe first command.")]
        public static string GettingStarted()
        {
            return PromptBody.Render(
                PromptBody.Load("getting_started"), GettingStartedSets, requiresSendCode: false);
        }

        [McpServerPrompt(Name = "revit_model_audit"),
         Description("Read-only model health audit over a scope: composite audit, family problems, warnings, dry-run purge candidates.")]
        public static string ModelAudit(
            [Description("Part of the model to audit; 'all' = whole model")] string scope = "all")
        {
            return PromptBody.Render(
                PromptBody.Load("model_audit"), ModelAuditSets, requiresSendCode: false,
                args: new Dictionary<string, string>
                {
                    ["scope"] = string.IsNullOrWhiteSpace(scope) ? "all" : scope
                });
        }

        [McpServerPrompt(Name = "revit_pre_issue_check"),
         Description("Read-only checks for resolved sheets: layout, tags, dimensions, revisions, and model-wide warning context. Incomplete checks are NOT VERIFIED.")]
        public static string PreIssueCheck(
            [Description("Sheet numbers/IDs, an explicit number/name filter, or 'all'; named sheet sets require their member sheets")] string scope = "all")
        {
            return PromptBody.Render(
                PromptBody.Load("pre_issue_check"), PreIssueSets, requiresSendCode: false,
                args: new Dictionary<string, string>
                {
                    ["scope"] = string.IsNullOrWhiteSpace(scope) ? "all" : scope
                });
        }

        [McpServerPrompt(Name = "revit_stairs"),
         Description("Guided stair creation via send_code: resolve the design together, confirm, then write and verify.")]
        public static string Stairs()
        {
            return PromptBody.Render(
                PromptBody.Load("stairs"), StairsSets, requiresSendCode: true);
        }
    }
}
