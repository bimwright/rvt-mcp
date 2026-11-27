using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RvtMcp.Plugin;

namespace RvtMcp.Server.Prompts
{
    internal sealed class WorkflowDefinition
    {
        public string Id { get; init; }
        public string PromptName { get; init; }
        public string SkillName { get; init; }
        public string SkillUri { get; init; }
        public string Description { get; init; }
        public string[] RequiredToolsets { get; init; }
        public bool RequiresSendCode { get; init; }
        public string[] RequiredTools { get; init; }
        public string ArgName { get; init; }
        public string DefaultPromptArg { get; init; }
        public string DefaultWorkflowArg { get; init; }
        public string ModeReadOnly { get; init; }
        public string ModeWritable { get; init; }
    }

    /// <summary>
    /// Single source of truth for bundled workflows across MCP Skills,
    /// <see cref="WorkflowGuideTools"/> (<c>revit_get_workflow</c>), and <see cref="RevitPrompts"/>.
    /// Keeps <c>change</c> (<c>skill://revit-change/SKILL.md</c>) first.
    /// </summary>
    internal static class WorkflowCatalog
    {
        private static readonly string[] GettingStartedSets = { "query", "meta" };
        private static readonly string[] ChangeSets = { "query", "meta" };
        private static readonly string[] ModelAuditSets = { "workflows", "families", "lint", "meta" };
        private static readonly string[] ModelAuditTools =
        {
            "revit_workflow_model_audit",
            "revit_audit_families",
            "revit_get_model_warnings_summary",
            "revit_purge_unused"
        };
        private static readonly string[] PreIssueSets = { "sheets", "view", "annotation", "lint", "meta" };
        private static readonly string[] DrawingLayoutSets = { "query", "sheets", "view", "meta" };
        // Spec §3: stairs needs meta (revit_get_current_target) even though send_code
        // can also ride the toolbaker set — without the gate a toolbaker-only server
        // would render a body whose first step isn't exposed.
        private static readonly string[] StairsSets = { "meta" };

        internal const string ChangeDescription =
            "Survey a requested model change, agree the smallest scope, confirm before writing, then read back and record why. Read-only sessions survey and propose only.";
        internal const string GettingStartedDescription =
            "Read-only orientation for the connected Revit: current target, active view, model statistics. Safe first command.";
        internal const string ModelAuditDescription =
            "Read-only model health audit over a scope: composite audit, family problems, warnings, dry-run purge candidates.";
        internal const string PreIssueCheckDescription =
            "Read-only checks for resolved sheets: layout, tags, dimensions, revisions, and model-wide warning context. Incomplete checks are NOT VERIFIED.";
        internal const string DrawingLayoutDescription =
            "Arrange viewports on one sheet: read real sheet-space positions, agree the alignment, confirm a dry-run plan, then move and read back. Read-only sessions report and propose only.";
        internal const string StairsDescription =
            "Guided stair creation via send_code: resolve the design together, confirm, then write and verify.";

        internal static readonly WorkflowDefinition[] All =
        {
            new WorkflowDefinition
            {
                Id = "change",
                PromptName = "revit_change",
                SkillName = "revit-change",
                SkillUri = "skill://revit-change/SKILL.md",
                Description = ChangeDescription,
                RequiredToolsets = ChangeSets,
                RequiresSendCode = false,
                ArgName = "change",
                DefaultPromptArg = "Not specified. Ask me what change I want before surveying or writing.",
                DefaultWorkflowArg = "Use the user's requested change from the conversation. If unspecified, ask before surveying or writing.",
                ModeReadOnly = "READ-ONLY: survey and propose only. Do not write or change the session configuration.",
                ModeWritable = "Use only exposed tools. Writes require confirmation of the concrete proposal below."
            },
            new WorkflowDefinition
            {
                Id = "getting_started",
                PromptName = "revit_getting_started",
                SkillName = "revit-getting-started",
                SkillUri = "skill://revit-getting-started/SKILL.md",
                Description = GettingStartedDescription,
                RequiredToolsets = GettingStartedSets,
                RequiresSendCode = false
            },
            new WorkflowDefinition
            {
                Id = "model_audit",
                PromptName = "revit_model_audit",
                SkillName = "revit-model-audit",
                SkillUri = "skill://revit-model-audit/SKILL.md",
                Description = ModelAuditDescription,
                RequiredToolsets = ModelAuditSets,
                RequiresSendCode = false,
                RequiredTools = ModelAuditTools,
                ArgName = "scope",
                DefaultPromptArg = "all",
                DefaultWorkflowArg = "the requested scope (or 'all' for the whole model)"
            },
            new WorkflowDefinition
            {
                Id = "pre_issue_check",
                PromptName = "revit_pre_issue_check",
                SkillName = "revit-pre-issue-check",
                SkillUri = "skill://revit-pre-issue-check/SKILL.md",
                Description = PreIssueCheckDescription,
                RequiredToolsets = PreIssueSets,
                RequiresSendCode = false,
                ArgName = "scope",
                DefaultPromptArg = "all",
                DefaultWorkflowArg = "the requested sheets or filter (or 'all' if the user asks for all sheets)"
            },
            new WorkflowDefinition
            {
                Id = "drawing_layout",
                PromptName = "revit_drawing_layout",
                SkillName = "revit-drawing-layout",
                SkillUri = "skill://revit-drawing-layout/SKILL.md",
                Description = DrawingLayoutDescription,
                RequiredToolsets = DrawingLayoutSets,
                RequiresSendCode = false,
                ArgName = "request",
                DefaultPromptArg = "Not specified. Ask me which sheet and which viewports, and how to arrange them, before reading or moving anything.",
                DefaultWorkflowArg = "Use the user's requested sheet layout from the conversation. If unspecified, ask which sheet, viewports, and alignment before reading or moving anything.",
                ModeReadOnly = "READ-ONLY: report positions and propose moves only. Do not call revit_align_viewports.",
                ModeWritable = "Use only exposed tools. Moves need confirmation of the dry-run plan below."
            },
            new WorkflowDefinition
            {
                Id = "stairs",
                PromptName = "revit_stairs",
                SkillName = "revit-stairs",
                SkillUri = "skill://revit-stairs/SKILL.md",
                Description = StairsDescription,
                RequiredToolsets = StairsSets,
                RequiresSendCode = true
            }
        };

        internal static bool TryFind(string query, out WorkflowDefinition def)
        {
            def = null;
            if (string.IsNullOrWhiteSpace(query)) return false;
            var key = query.Trim();
            foreach (var item in All)
            {
                if (string.Equals(key, item.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, item.PromptName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, item.SkillName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, item.SkillUri, StringComparison.OrdinalIgnoreCase))
                {
                    def = item;
                    return true;
                }
            }
            return false;
        }

        internal static bool TryFindBySkillUri(string uri, out WorkflowDefinition def)
        {
            def = null;
            if (string.IsNullOrWhiteSpace(uri)) return false;
            var key = uri.Trim();
            foreach (var item in All)
            {
                if (string.Equals(key, item.SkillUri, StringComparison.OrdinalIgnoreCase))
                {
                    def = item;
                    return true;
                }
            }
            return false;
        }

        internal static string RenderForPrompt(string id, string argValue = null)
        {
            if (!TryFind(id, out var def))
                return "This workflow's body was not found in the installed server build.";

            var args = BuildArgs(def, argValue, def.DefaultPromptArg, ServerState.Config);
            return PromptBody.Render(
                PromptBody.Load(def.Id),
                def.RequiredToolsets,
                def.RequiresSendCode,
                args: args,
                requiredTools: def.RequiredTools);
        }

        internal static string RenderBody(WorkflowDefinition def, HashSet<string> enabled, RvtMcpConfig config)
        {
            var args = BuildArgs(def, null, def.DefaultWorkflowArg, config);
            var rendered = PromptBody.Render(
                PromptBody.Load(def.Id),
                def.RequiredToolsets,
                def.RequiresSendCode,
                enabled,
                config,
                args: args,
                requiredTools: def.RequiredTools);
            return rendered.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        internal static string BuildSkillMarkdown(WorkflowDefinition def, HashSet<string> enabled, RvtMcpConfig config)
        {
            var body = RenderBody(def, enabled, config);
            return "---\nname: " + def.SkillName + "\ndescription: " + JsonConvert.ToString(def.Description) + "\n---\n\n" + body;
        }

        internal static bool IsAvailable(
            WorkflowDefinition def,
            HashSet<string> enabled,
            RvtMcpConfig config,
            out string unavailableNotice)
        {
            return !PromptBody.TryGetUnavailableNotice(
                def.RequiredToolsets,
                def.RequiresSendCode,
                def.RequiredTools,
                enabled,
                config,
                out unavailableNotice);
        }

        private static Dictionary<string, string> BuildArgs(
            WorkflowDefinition def,
            string suppliedArg,
            string fallbackArg,
            RvtMcpConfig config)
        {
            if (def.ArgName == null && def.ModeReadOnly == null && def.ModeWritable == null)
                return null;

            var args = new Dictionary<string, string>();
            if (def.ModeReadOnly != null || def.ModeWritable != null)
            {
                args["mode"] = config?.ReadOnlyOrDefault == true
                    ? (def.ModeReadOnly ?? string.Empty)
                    : (def.ModeWritable ?? string.Empty);
            }
            if (def.ArgName != null)
            {
                args[def.ArgName] = string.IsNullOrWhiteSpace(suppliedArg)
                    ? (fallbackArg ?? string.Empty)
                    : suppliedArg;
            }
            return args;
        }
    }
}
