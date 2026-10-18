using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;
using Xunit.Abstractions;

namespace RvtMcp.Tests
{
    public class ToolSurfaceBudgetTests
    {
        private readonly ITestOutputHelper _output;
        public ToolSurfaceBudgetTests(ITestOutputHelper output) => _output = output;

        // Canonical descriptor benchmark: omit volatile health runtime identity, sort names,
        // retain descriptions, annotations and SDK-generated schemas. Bytes are not model tokens.
        internal static string SerializeSurface(RvtMcpConfig config)
        {
            var tools = Program.CreateRegisteredTools(ToolsetFilter.Resolve(config), config)
                .Select(tool => tool.ProtocolTool).OrderBy(tool => tool.Name, System.StringComparer.Ordinal).ToArray();
            foreach (var tool in tools) tool.Meta = null;
            return JsonSerializer.Serialize(new ListToolsResult
            {
                Tools = tools
            }, McpJsonUtilities.DefaultOptions);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Full_surface_reports_protocol_bytes(bool all, bool readOnly)
        {
            var config = new RvtMcpConfig { ReadOnly = readOnly,
                Toolsets = all ? new List<string> { "all" } : null };
            var surface = SerializeSurface(config);
            var bytes = Encoding.UTF8.GetByteCount(surface);
            using var document = JsonDocument.Parse(surface);
            var count = document.RootElement.GetProperty("tools").GetArrayLength();
            _output.WriteLine($"descriptor all={all} readOnly={readOnly}: tools={count}; bytes={bytes}; runtime metadata excluded");
            Assert.True(bytes > 0);
        }

        [Fact]
        public void Full_instructions_fit_discovery_budget()
        {
            var text = (string)typeof(Program).GetField("ServerInstructionsText",
                BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            Assert.True(Encoding.UTF8.GetByteCount(text) < 2048);
        }

        [Fact]
        public void Core_descriptions_fit_per_tool_budget()
        {
            var core = new[] { "revit_health", "revit_list_available_targets", "revit_switch_target",
                "revit_get_current_target", "revit_get_current_view_info", "revit_get_selected_elements",
                "revit_capture_view_image", "revit_list_recent_models", "revit_open_model",
                "revit_get_operation_status", "revit_send_code_to_revit" };
            var config = new RvtMcpConfig { Toolsets = new List<string> { "all" } };
            foreach (var tool in Program.CreateRegisteredTools(ToolsetFilter.Resolve(config), config)
                .Where(tool => core.Contains(tool.ProtocolTool.Name)))
                Assert.True(tool.ProtocolTool.Description.Length <= 600, tool.ProtocolTool.Name);
        }
    }
}
