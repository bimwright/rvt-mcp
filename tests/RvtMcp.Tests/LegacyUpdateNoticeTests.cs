using System.Reflection;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class LegacyUpdateNoticeTests
    {
        [Fact]
        public void Server_instructions_lead_with_the_legacy_notice()
        {
            var programType = typeof(ToolsetFilter).Assembly.GetType("RvtMcp.Server.Program")!;
            var field = programType.GetField("ServerInstructionsText", BindingFlags.NonPublic | BindingFlags.Static)!;
            var text = (string)(field.GetRawConstantValue() ?? field.GetValue(null)!);
            Assert.StartsWith(LegacyUpdateNotice.InstructionsPrefix, text);
            Assert.Contains("https://github.com/bimwright/rvt-mcp/releases/latest", text);
        }

        [Fact]
        public void AttachOnce_adds_the_field_to_the_first_response_only()
        {
            LegacyUpdateNotice.ResetForTests();
            var first = new JObject { ["viewName"] = "Level 1" };
            var second = new JObject { ["viewName"] = "Level 2" };

            LegacyUpdateNotice.AttachOnce(first);
            LegacyUpdateNotice.AttachOnce(second);

            Assert.Equal(LegacyUpdateNotice.Text, (string)first["update_notice"]);
            Assert.Equal("Level 1", (string)first["viewName"]);
            Assert.Null(second["update_notice"]);
            LegacyUpdateNotice.ResetForTests();
        }

        [Fact]
        public void Notice_does_not_hardcode_a_version_number()
        {
            Assert.DoesNotMatch(@"\d+\.\d+\.\d+", LegacyUpdateNotice.Text);
            Assert.DoesNotMatch(@"\d+\.\d+\.\d+", LegacyUpdateNotice.InstructionsPrefix);
        }
    }
}
