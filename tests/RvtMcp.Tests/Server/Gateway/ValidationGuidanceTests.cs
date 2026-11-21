using System;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class ValidationGuidanceTests
    {
        // Seen live (#43): the add-in's validation hint listed the allowed reference modes, but the agent
        // received only "Error: Parameter validation failed: required field 'references[0].mode' is missing".
        [Fact]
        public void A_validation_failure_keeps_the_suggestion_and_hint_in_the_error()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ToolGateway.InterpretResponse(new JObject
            {
                ["success"] = false,
                ["error"] = "Parameter validation failed: required field 'references[0].mode' is missing",
                ["suggestion"] = "Add 'mode' to each item",
                ["hint"] = "references: array of {mode (required): one of [\"wall_centerline\", \"grid_curve\"]}",
            }));

            Assert.StartsWith("Parameter validation failed: required field 'references[0].mode' is missing", ex.Message);
            Assert.Contains("Suggestion: Add 'mode' to each item", ex.Message);
            Assert.Contains("Hint: references: array of {mode (required): one of [\"wall_centerline\"", ex.Message);
        }

        [Fact]
        public void A_failure_without_guidance_keeps_its_error_unchanged()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ToolGateway.InterpretResponse(new JObject
            {
                ["success"] = false,
                ["error"] = "Element not found.",
            }));

            Assert.Equal("Element not found.", ex.Message);
        }
    }
}
