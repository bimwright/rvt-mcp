using System.Collections.Generic;
using System.Threading.Tasks;
using Bimwright.Targeting;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class ReadOnlyTargetContractTests
    {
        [Fact]
        public void Read_only_surface_excludes_session_navigation()
        {
            var config = new RvtMcpConfig { ReadOnly = true, Toolsets = new List<string> { "all" } };
            var tools = Program.CreateRegisteredTools(ToolsetFilter.Resolve(config), config);
            Assert.DoesNotContain(tools, tool => tool.ProtocolTool.Name == "revit_switch_target");
            Assert.All(tools, tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint == true,
                tool.ProtocolTool.Name));
            Assert.Contains(tools, tool => tool.ProtocolTool.Name == "revit_list_available_targets");
            Assert.Contains(tools, tool => tool.ProtocolTool.Name == "revit_get_current_target");
        }

        [Fact]
        public async Task Direct_switch_target_in_read_only_never_parses_or_changes_binding()
        {
            var previous = ServerState.Config;
            try
            {
                ServerState.Config = new RvtMcpConfig { ReadOnly = true };
                var generation = RevitTargetBinding.CaptureGeneration();
                var result = JObject.Parse(await MetaTools.SwitchTarget("invalid-selector"));
                Assert.Equal("read_only_mode", result.Value<string>("error"));
                Assert.Equal("switch_target", result.Value<string>("tool"));
                Assert.Equal(generation, RevitTargetBinding.CaptureGeneration());
            }
            finally { ServerState.Config = previous; }
        }

        [Fact]
        public void Read_only_target_refusal_explains_startup_selection_instead_of_hidden_tool()
        {
            var previous = ServerState.Config;
            try
            {
                ServerState.Config = new RvtMcpConfig { ReadOnly = true };
                var result = MetaTools.TargetEnvelope(new TargetPayload
                {
                    Code = TargetCode.AmbiguousTarget,
                    Message = "Choose a process with revit_switch_target.",
                    NextStep = "Call revit_switch_target after listing candidates."
                });
                Assert.Contains("--target", result.ToString());
                Assert.DoesNotContain("revit_switch_target", result.ToString());
            }
            finally { ServerState.Config = previous; }
        }
    }
}
