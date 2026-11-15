using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests.Server.Tools
{
    public class ShowIssueToolTests
    {
        [Fact]
        public async Task Sends_incident_and_suggested_action_to_the_add_in()
        {
            var sent = await Capture.Send(() => FailureTools.ShowIssue("incident-1", "create_rule"));

            Assert.Equal("show_issue", sent.Command);
            var json = sent.Json();
            Assert.Equal("incident-1", json.Value<string>("incident_id"));
            Assert.Equal("create_rule", json.Value<string>("action"));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("ledger/../x", null)]
        [InlineData("incident-1", "approve_everything")]
        public async Task Rejects_bad_arguments_without_reaching_revit(string incidentId, string action)
        {
            var sent = false;
            string result = null;
            await Capture.WithSendOverride((c, p, t) => { sent = true; return Task.FromResult(new JObject()); },
                async () => result = await FailureTools.ShowIssue(incidentId, action));

            Assert.False(sent);
            Assert.Contains("INVALID_ARGUMENT", result);
        }
    }
}
