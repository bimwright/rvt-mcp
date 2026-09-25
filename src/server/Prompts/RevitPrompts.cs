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
        internal static readonly string[] GettingStartedSets = { "query", "meta" };
        internal static readonly string[] ModelAuditSets = { "workflows", "families", "lint", "meta" };

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
    }
}
