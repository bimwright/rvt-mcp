using System;
using System.Collections.Generic;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Prompts;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>Spec D2: missing toolsets → enable notice replaces the body;
    /// send_code gate → its own notice; args substitute after gating.</summary>
    [Collection("ServerStateConfig")]
    public class PromptBodyTests : IDisposable
    {
        private const string Body =
            "# Demo\n\nSteps\n1. call revit_get_current_view_info for {scope}.\n\nDo not\n- write.";

        public PromptBodyTests() { ServerState.Config = new RvtMcpConfig(); }
        public void Dispose() { ServerState.Config = null; }

        [Fact]
        public void Load_returns_null_for_missing_resource()
        {
            Assert.Null(PromptBody.Load("no_such_prompt"));
        }

        [Fact]
        public void Render_returns_enable_notice_when_toolsets_missing()
        {
            var rendered = PromptBody.Render(Body, new[] { "workflows", "families", "lint" }, false);

            Assert.Contains("workflows", rendered);
            Assert.Contains("families", rendered);
            Assert.Contains("lint", rendered);
            Assert.Contains("--toolsets", rendered);
            Assert.Contains("restart", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("call revit_get_current_view_info", rendered);
        }

        [Fact]
        public void Enable_notice_suggests_merged_csv_in_known_toolset_order()
        {
            // Default config enables query,create,view,meta.
            var rendered = PromptBody.Render(Body, new[] { "workflows", "families", "lint" }, false);

            Assert.Contains(
                "--toolsets query,create,view,families,meta,lint,workflows",
                rendered.Replace("  ", " "));
        }

        [Fact]
        public void Render_returns_body_with_args_applied_when_all_enabled()
        {
            ServerState.Config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "all" }
            };
            var args = new Dictionary<string, string> { ["scope"] = "level 2" };

            var rendered = PromptBody.Render(Body, new[] { "workflows" }, false, args);

            Assert.Equal(Body.Replace("{scope}", "level 2"), rendered);
        }

        [Fact]
        public void Enable_notice_warns_when_read_only_would_restrip_write_capable_sets()
        {
            ServerState.Config = new RvtMcpConfig { ReadOnly = true };

            var rendered = PromptBody.Render(Body, new[] { "workflows" }, false);

            Assert.Contains("--read-only", rendered);
            Assert.Contains("write-capable", rendered);
        }

        [Fact]
        public void Render_returns_sendcode_notice_under_read_only()
        {
            ServerState.Config = new RvtMcpConfig { ReadOnly = true };

            var rendered = PromptBody.Render(Body, null, requiresSendCode: true);

            Assert.Contains("--read-only", rendered);
            Assert.Contains("revit_send_code_to_revit", rendered);
            Assert.DoesNotContain("call revit_get_current_view_info", rendered);
        }

        [Fact]
        public void Render_returns_sendcode_notice_when_meta_and_toolbaker_absent()
        {
            ServerState.Config = new RvtMcpConfig
            {
                Toolsets = new List<string> { "query" }
            };

            var rendered = PromptBody.Render(Body, null, requiresSendCode: true);

            Assert.Contains("revit_send_code_to_revit", rendered);
            Assert.DoesNotContain("call revit_get_current_view_info", rendered);
        }

        [Fact]
        public void ApplyArgs_leaves_unmatched_placeholders_and_null_is_noop()
        {
            var args = new Dictionary<string, string> { ["scope"] = "x" };
            Assert.Equal("a {other} x", PromptBody.ApplyArgs("a {other} {scope}", args));
            Assert.Equal(Body, PromptBody.ApplyArgs(Body, null));
        }
    }
}
