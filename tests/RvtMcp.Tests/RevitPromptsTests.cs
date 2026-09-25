using System;
using System.Collections.Generic;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Prompts;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class RevitPromptsTests : IDisposable
    {
        public RevitPromptsTests() { ServerState.Config = new RvtMcpConfig(); }
        public void Dispose() { ServerState.Config = null; }

        [Theory]
        [InlineData("getting_started")]
        [InlineData("model_audit")]
        [InlineData("pre_issue_check")]
        [InlineData("stairs")]
        public void Embedded_prompt_body_loads(string name)
        {
            Assert.False(string.IsNullOrWhiteSpace(PromptBody.Load(name)), $"missing RvtMcp.Prompts.{name}.md");
        }

        [Fact]
        public void GettingStarted_renders_steps_under_default_config()
        {
            var rendered = RevitPrompts.GettingStarted();

            Assert.Contains("revit_get_current_view_info", rendered);
            Assert.Contains("revit_analyze_model_statistics", rendered);
            Assert.DoesNotContain("--toolsets", rendered);
        }

        [Theory]
        [InlineData("meta", "query")]
        [InlineData("query", "meta")]
        public void GettingStarted_missing_read_only_toolset_does_not_require_disabling_read_only(
            string enabledSet, string missingSet)
        {
            ServerState.Config = new RvtMcpConfig
            {
                ReadOnly = true,
                Toolsets = new List<string> { enabledSet }
            };

            var notice = RevitPrompts.GettingStarted();

            Assert.Contains("Missing: " + missingSet, notice);
            Assert.Contains("--toolsets query,meta", notice);
            Assert.DoesNotContain("--read-only", notice);
            Assert.DoesNotContain("# Getting started", notice);

            ServerState.Config.Toolsets.Add(missingSet);
            Assert.True(ServerState.Config.ReadOnlyOrDefault);
            Assert.Contains("# Getting started", RevitPrompts.GettingStarted());
        }

        [Fact]
        public void ModelAudit_returns_enable_notice_under_default_config()
        {
            var rendered = RevitPrompts.ModelAudit();

            Assert.Contains("workflows", rendered);
            Assert.Contains("families", rendered);
            Assert.Contains("lint", rendered);
            Assert.Contains("--toolsets", rendered);
            Assert.DoesNotContain("revit_workflow_model_audit", rendered);
        }

        [Fact]
        public void ModelAudit_renders_body_with_scope_substituted_under_all_toolsets()
        {
            ServerState.Config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "all" }
            };

            var rendered = RevitPrompts.ModelAudit("the HVAC wing");

            Assert.Contains("the HVAC wing", rendered);
            Assert.Contains("revit_workflow_model_audit", rendered);
            Assert.Contains("dry_run", rendered);
            Assert.DoesNotContain("{scope}", rendered);
        }

        [Fact]
        public void ModelAudit_empty_scope_falls_back_to_all()
        {
            ServerState.Config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "all" }
            };

            var rendered = RevitPrompts.ModelAudit("");

            Assert.DoesNotContain("{scope}", rendered);
            Assert.Contains("all", rendered);
        }

        [Fact]
        public void PreIssueCheck_returns_enable_notice_under_default_config()
        {
            var rendered = RevitPrompts.PreIssueCheck();

            Assert.Contains("annotation", rendered);
            Assert.Contains("lint", rendered);
            Assert.Contains("sheets", rendered);
            Assert.Contains("--toolsets", rendered);
            Assert.DoesNotContain("revit_list_sheets", rendered);
        }

        [Fact]
        public void PreIssueCheck_renders_body_with_scope_under_all_toolsets()
        {
            ServerState.Config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "all" }
            };

            var rendered = RevitPrompts.PreIssueCheck("ISSUE-SET-01");

            Assert.Contains("ISSUE-SET-01", rendered);
            Assert.Contains("revit_list_sheets", rendered);
            Assert.Contains("revit_list_revisions", rendered);
            Assert.DoesNotContain("{scope}", rendered);
        }

        [Fact]
        public void PreIssueCheck_requires_member_sheets_instead_of_guessing_a_named_sheet_set()
        {
            ServerState.Config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "all" }
            };

            var rendered = RevitPrompts.PreIssueCheck("ISSUE-SET-01");

            Assert.Contains("Requested scope: ISSUE-SET-01.", rendered);
            Assert.Contains("cannot resolve saved sheet-set membership", rendered);
            Assert.Contains("stop and ask me for its member sheet numbers or IDs", rendered);
            Assert.Contains("Do not treat a sheet set name as numberFilter or namePattern", rendered);
            Assert.DoesNotContain("sheets in ISSUE-SET-01", rendered);
        }

        [Fact]
        public void PreIssueCheck_marks_sampled_or_incomplete_evidence_as_not_verified()
        {
            ServerState.Config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "sheets", "view", "annotation", "lint", "meta" }
            };

            var rendered = RevitPrompts.PreIssueCheck("A-101");

            Assert.Contains("model-wide warning context", rendered);
            Assert.Contains("not a view-filtered warning list", rendered);
            Assert.Contains("absence from examples does not prove absence of warnings", rendered);
            Assert.Contains("even when truncated=false", rendered);
            Assert.Contains("Per-sheet warning status is NOT VERIFIED when total_warnings > 0", rendered);
            Assert.Contains("Only a successful total_warnings=0 establishes absence of model warnings", rendered);
            Assert.Contains("sheet | check | PASS/FAIL/NOT VERIFIED | element ids | coverage", rendered);
            Assert.Contains("limit_hit", rendered);
            Assert.Contains("skipped", rendered);
            Assert.DoesNotContain("only warnings touching those views", rendered);
        }

        [Fact]
        public void Stairs_renders_body_under_default_config()
        {
            var rendered = RevitPrompts.Stairs();

            Assert.Contains("revit_send_code_to_revit", rendered);
            Assert.Contains("confirm", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--toolsets", rendered);
        }

        [Fact]
        public void Stairs_includes_failure_and_cleanup_template_without_checkout_docs()
        {
            var rendered = RevitPrompts.Stairs();

            Assert.DoesNotContain("docs/", rendered);
            Assert.Contains("```csharp", rendered);
            Assert.Contains("new RvtMcp.Plugin.SafeFailuresPreprocessor()", rendered);
            Assert.Contains("new StairsEditScope(doc", rendered);
            Assert.Contains("new Transaction(doc", rendered);
            Assert.Contains("SetFailuresPreprocessor(failures).SetClearAfterRollback(true)", rendered);
            Assert.Contains("txStatus != TransactionStatus.Committed", rendered);
            Assert.Contains("scope.Commit(failures)", rendered);
            Assert.Contains("!failures.HadErrors", rendered);
            Assert.Contains("doc.GetElement(stairsId) != null", rendered);
            Assert.Contains("failures.Messages", rendered);
            Assert.Contains("finally", rendered);
            Assert.Contains("if (scope.IsActive) scope.Cancel();", rendered);
            Assert.Contains("not a complete stair generator", rendered);
            Assert.Contains("Get my confirmation before writing anything", rendered);
        }

        [Fact]
        public void Stairs_returns_sendcode_notice_under_read_only()
        {
            ServerState.Config = new RvtMcpConfig { ReadOnly = true };

            var rendered = RevitPrompts.Stairs();

            Assert.Contains("--read-only", rendered);
            Assert.DoesNotContain("resolved design", rendered);
        }

        [Fact]
        public void Stairs_returns_enable_notice_when_meta_off_even_with_toolbaker()
        {
            // send_code stays available via the toolbaker set, but the body's step-0
            // revit_get_current_target lives in meta — the prompt must gate on it.
            ServerState.Config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "toolbaker" }
            };

            var rendered = RevitPrompts.Stairs();

            Assert.Contains("meta", rendered);
            Assert.Contains("--toolsets", rendered);
            Assert.DoesNotContain("resolved design", rendered);
        }
    }
}
