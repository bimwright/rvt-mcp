using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class ActiveDocumentReceiptTests
    {
        [Theory]
        [InlineData("A", "B", true)]
        [InlineData("A", "A", false)]
        [InlineData(null, "B", true)]
        [InlineData("A", null, true)]
        [InlineData(null, null, false)]
        public void Cancelled_model_operation_still_reports_observed_document_switch(string before, string after, bool changed)
        {
            var operation = new FailureOperation { Execution = "completed", ActiveModelBefore = before, ActiveModelAfter = after };
            FailureCompletion.Apply(operation, new FailureCompletionFacts { ExecutionStarted = true,
                NativeCancelled = true, PolicyStopped = true, ActiveDocumentObserved = true });
            Assert.Equal("cancelled", operation.Outcome);
            Assert.False(operation.MutationApplied);
            Assert.Equal(changed, operation.ActiveDocumentChanged);
        }

        [Fact]
        public void Failed_observation_does_not_claim_a_document_switch()
        {
            var operation = new FailureOperation { Execution = "completed", ActiveModelBefore = "A", ActiveModelAfter = null };
            FailureCompletion.Apply(operation, new FailureCompletionFacts { ExecutionStarted = true });
            Assert.Null(operation.ActiveDocumentChanged);
        }

        [Fact]
        public void Document_switch_survives_compaction_and_late_result_delivery()
        {
            var operation = new JObject { ["execution"] = "completed", ["outcome"] = "cancelled",
                ["mutationApplied"] = false, ["activeDocumentChanged"] = true,
                ["activeModelBefore"] = "A", ["activeModelAfter"] = "B", ["complete"] = true };
            var compact = SafetyResponseMetadata.Bound(operation, 1024, compact: true);
            Assert.True(compact.Value<bool>("activeDocumentChanged"));
            var tracker = new PreviousOperationTracker();
            tracker.Remember("switch", "send_code_to_revit", Task.FromResult(new JObject {
                ["success"] = false, ["_operation"] = operation }.ToString()));
            Assert.True(tracker.Status("switch").Value<bool>("activeDocumentChanged"));
            var response = new JObject();
            tracker.Attach(response);
            Assert.True(response["_previousOperation"][0].Value<bool>("activeDocumentChanged"));
        }
    }
}
