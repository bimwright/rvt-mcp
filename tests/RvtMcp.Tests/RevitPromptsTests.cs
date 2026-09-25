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
        public void Stairs_renders_body_under_default_config()
        {
            var rendered = RevitPrompts.Stairs();

            Assert.Contains("revit_send_code_to_revit", rendered);
            Assert.Contains("confirm", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--toolsets", rendered);
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
