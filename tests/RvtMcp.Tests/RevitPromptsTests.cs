using System;
using System.Collections.Generic;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Prompts;
using Xunit;

namespace RvtMcp.Tests
{
    public class RevitPromptsTests : IDisposable
    {
        public RevitPromptsTests() { ServerState.Config = new RvtMcpConfig(); }
        public void Dispose() { ServerState.Config = null; }

        [Theory]
        [InlineData("getting_started")]
        [InlineData("model_audit")]
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
    }
}
