using System;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// An add-in that predates a tool answers "Unknown command: name". The server turns that into guidance
    /// that names the Revit year and the next step.
    /// </summary>
    public class UnknownCommandGuidanceTests
    {
        [Fact]
        public void Unknown_command_error_names_the_command_the_year_and_the_fix()
        {
            var text = ToolGateway.UnknownCommandGuidance("Unknown command: get_viewport_geometry", "2027");

            Assert.NotNull(text);
            Assert.Contains("Revit 2027", text);
            Assert.Contains("'get_viewport_geometry'", text);
            Assert.Contains("older build", text);
            Assert.Contains("revit_list_available_targets", text);
            Assert.Contains("revit_switch_target", text);
        }

        [Fact]
        public void Unknown_year_falls_back_to_a_generic_label()
        {
            var text = ToolGateway.UnknownCommandGuidance("Unknown command: align_viewports", null);

            Assert.Contains("this Revit", text);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Sheet could not be resolved.")]
        [InlineData("Unknown command: ")]
        [InlineData("Unknown command: two words")]
        [InlineData("unknown command: lower_case_prefix")]
        public void Other_errors_are_left_alone(string error)
        {
            Assert.Null(ToolGateway.UnknownCommandGuidance(error, "2024"));
        }

        [Fact]
        public void A_failed_response_surfaces_the_guidance_as_the_exception_message()
        {
            var response = new JObject { ["success"] = false, ["error"] = "Unknown command: get_viewport_geometry" };

            var ex = Assert.Throws<InvalidOperationException>(() => ToolGateway.InterpretResponse(response));

            Assert.Contains("does not know the command 'get_viewport_geometry'", ex.Message);
        }

        [Fact]
        public void Ordinary_failures_keep_their_original_message()
        {
            var response = new JObject { ["success"] = false, ["error"] = "Sheet could not be resolved." };

            var ex = Assert.Throws<InvalidOperationException>(() => ToolGateway.InterpretResponse(response));

            Assert.Equal("Sheet could not be resolved.", ex.Message);
        }
    }
}
