using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class BasePointFailureReceiptTests
    {
        [Fact]
        public void Partial_coordinate_failure_keeps_actual_values_and_failed_completion()
        {
            var operation = new FailureOperation { Execution = "completed" };
            FailureCompletion.Apply(operation, new FailureCompletionFacts {
                ExecutionStarted = true, HandlerSucceeded = false, AnyCommittedChanges = true });
            var actual = new JObject { ["east_west_mm"] = 417622267.22, ["north_south_mm"] = 78713983.149, ["elevation_mm"] = 333.0 };
            var envelope = new JObject {
                ["success"] = false, ["error"] = "Coordinates not fully applied",
                ["data"] = new JObject { ["updated"] = false, ["partially_updated"] = true, ["after"] = actual },
                ["_operation"] = JObject.Parse(FailureJson.Serialize(operation)) };
            var result = ToolGateway.InterpretResponse(envelope);
            Assert.False(result.Value<bool>("success"));
            Assert.True(result.Value<bool>("partially_updated"));
            Assert.True(JToken.DeepEquals(actual, result["after"]));
            Assert.Equal("partial", result["_operation"]["outcome"]);
            Assert.True(result["_operation"].Value<bool>("mutationApplied"));
        }
    }
}
