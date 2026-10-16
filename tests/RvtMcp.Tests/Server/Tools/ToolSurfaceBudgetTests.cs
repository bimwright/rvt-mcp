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

        // Measure protocol tools, including descriptions and SDK-generated schemas, not golden hashes.
        internal static string SerializeSurface(RvtMcpConfig config)
            => JsonSerializer.Serialize(new ListToolsResult
            {
                Tools = Program.CreateRegisteredTools(ToolsetFilter.Resolve(config), config)
                    .Select(tool => tool.ProtocolTool).ToArray()
            }, McpJsonUtilities.DefaultOptions);

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Full_surface_reports_protocol_bytes(bool all, bool readOnly)
        {
            var config = new RvtMcpConfig { ReadOnly = readOnly,
                Toolsets = all ? new List<string> { "all" } : null };
            var bytes = Encoding.UTF8.GetByteCount(SerializeSurface(config));
            _output.WriteLine($"full all={all} readOnly={readOnly}: {bytes} bytes; ~{bytes / 4} tokens (estimate)");
            Assert.True(bytes > 0);
        }

        [Fact]
        public void Full_instructions_fit_discovery_budget()
        {
            var text = (string)typeof(Program).GetField("ServerInstructionsText",
                BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            Assert.True(Encoding.UTF8.GetByteCount(text) < 2048);
        }
    }
}
