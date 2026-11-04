using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Views.Toast;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class DocumentToolSurfaceTests
    {
        private static readonly string[] Writes = { "revit_save_document", "revit_save_checkpoint", "revit_manage_checkpoints" };

        private static ModelContextProtocol.Server.McpServerTool[] Tools(RvtMcpConfig config) =>
            Program.CreateRegisteredTools(ToolsetFilter.Resolve(config), config);

        [Fact]
        public void Document_toolset_is_opt_in_and_state_is_available_in_default_and_read_only()
        {
            Assert.Contains("documents", ToolsetFilter.KnownToolsets);
            Assert.Contains("documents", ToolsetFilter.WriteCapable);
            var defaults = Tools(new RvtMcpConfig()).Select(t => t.ProtocolTool.Name).ToArray();
            Assert.Contains("revit_get_document_state", defaults);
            Assert.All(Writes, name => Assert.DoesNotContain(name, defaults));

            var all = new RvtMcpConfig { Toolsets = new List<string> { "all" } };
            var allNames = Tools(all).Select(t => t.ProtocolTool.Name).ToArray();
            Assert.All(Writes, name => Assert.Contains(name, allNames));
            var selected = new RvtMcpConfig { Toolsets = new List<string> { "documents" } };
            var selectedNames = Tools(selected).Select(t => t.ProtocolTool.Name).ToArray();
            Assert.All(Writes, name => Assert.Contains(name, selectedNames));
            Assert.DoesNotContain("revit_get_document_state", selectedNames);
            var readOnly = new RvtMcpConfig { Toolsets = new List<string> { "all" }, ReadOnly = true };
            var readNames = Tools(readOnly).Select(t => t.ProtocolTool.Name).ToArray();
            Assert.Contains("revit_get_document_state", readNames);
            Assert.All(Writes, name => Assert.DoesNotContain(name, readNames));
        }

        [Fact]
        public void Annotations_and_plugin_read_policy_match_file_effects()
        {
            var config = new RvtMcpConfig { Toolsets = new List<string> { "all" } };
            var tools = Tools(config).Select(t => t.ProtocolTool).ToArray();
            var state = Assert.Single(tools, t => t.Name == "revit_get_document_state");
            Assert.True(state.Annotations.ReadOnlyHint);
            Assert.False(state.Annotations.DestructiveHint);
            Assert.True(state.Annotations.IdempotentHint);
            Assert.True(ToolReadPolicy.IsReadOnly("get_document_state"));
            foreach (var name in Writes)
            {
                var tool = Assert.Single(tools, t => t.Name == name);
                Assert.False(tool.Annotations.ReadOnlyHint);
                Assert.True(tool.Annotations.DestructiveHint);
                Assert.False(ToolReadPolicy.IsReadOnly(name.Substring("revit_".Length)));
                Assert.Equal(ToolActivityKind.Write, ToolActivityClassifier.Classify(name.Substring("revit_".Length)));
            }
            Assert.Contains("backup", Assert.Single(tools, t => t.Name == "revit_save_document").Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cannot be undone", Assert.Single(tools, t => t.Name == "revit_manage_checkpoints").Description, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Instructions_include_document_policy_and_fit_discovery_budget()
        {
            var text = (string)typeof(Program).GetField("ServerInstructionsText", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            Assert.Contains("Documents: call revit_get_document_state before saving and follow its policy; checkpoint only before risky work.", text);
            Assert.Contains("- documents: save_document, save_checkpoint", text);
            Assert.True(Encoding.UTF8.GetByteCount(text) < 2048);
        }
    }
}
