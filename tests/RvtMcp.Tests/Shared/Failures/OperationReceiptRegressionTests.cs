using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    public class OperationReceiptRegressionTests
    {
        [Theory]
        [InlineData("queued")]
        [InlineData("running")]
        [InlineData("waiting_for_user")]
        [InlineData("timed_out_running")]
        public void Pending_wire_receipt_cannot_claim_complete_capture(string execution)
        {
            var operation = new FailureOperation { Execution = execution, Complete = true };
            var receipt = JObject.Parse(FailureJson.Serialize(operation));
            Assert.False(receipt.Value<bool>("complete"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Finishing_preserves_the_actual_capture_completeness(bool captureComplete)
        {
            var operation = new FailureOperation { Execution = "waiting_for_user", Complete = captureComplete };
            Assert.False(JObject.Parse(FailureJson.Serialize(operation)).Value<bool>("complete"));
            operation.Execution = "completed";
            Assert.Equal(captureComplete, JObject.Parse(FailureJson.Serialize(operation)).Value<bool>("complete"));
        }
    }
}
