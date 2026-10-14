using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolNameFormatterTests
    {
        [Theory]
        [InlineData("create_grid", "Create Grid")]
        [InlineData("get_element_details", "Get Element Details")]
        [InlineData("export_ifc", "Export IFC")]
        [InlineData("analyze_mep_network", "Analyze MEP Network")]
        [InlineData("export_pdf", "Export PDF")]
        [InlineData("export_dwg", "Export DWG")]
        [InlineData("export_nwc", "Export NWC")]
        public void Format_PrettifiesToolNames(string commandName, string expected)
        {
            Assert.Equal(expected, ToolNameFormatter.Format(commandName));
        }

        [Fact]
        public void Format_Empty_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, ToolNameFormatter.Format(null));
            Assert.Equal(string.Empty, ToolNameFormatter.Format(""));
        }
    }
}
