using System;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    public class FailureReceiptScopeTests
    {
        [Theory]
        [InlineData(null, "open_model", "model_a", "model_b", "model_b", "model_b")]
        [InlineData(null, "open_model", null, "model_b", "model_b", "model_b")]
        [InlineData(null, "open_model", "model_a", "model_b", null, "model_b")]
        [InlineData(null, "open_model", "model_a", null, null, null)]
        [InlineData("model_b", "open_model", "model_a", "model_b|model_c", "model_c", "model_b")]
        [InlineData(null, "send_code_to_revit", "model_a", "model_a|model_b", "model_b", "model_b")]
        [InlineData(null, "batch_execute", "model_a", "model_a|model_b", "model_a|model_b", null)]
        [InlineData(null, "get_current_view_info", "model_a", "model_a", null, "model_a")]
        [InlineData(null, "open_model", "model_a", "model_b", "|model_b|model_b", "model_b")]
        public void ReceiptIdentifiesAffectedModelOrLeavesAmbiguousScopeUnset(string explicitScope, string tool,
            string previous, string claimed, string incidents, string expected)
        {
            Assert.Equal(expected, FailureCapture.ReceiptModelScopeKey(explicitScope, tool, previous,
                Split(claimed), Split(incidents)));
        }

        private static string[] Split(string value) => value == null ? Array.Empty<string>() : value.Split('|');
    }
}
