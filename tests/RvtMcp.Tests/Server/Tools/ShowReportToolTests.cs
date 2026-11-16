using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests.Server.Tools
{
    public class ShowReportToolTests
    {
        [Fact]
        public async Task Sends_report_id_to_the_add_in()
        {
            var sent = await Capture.Send(() => ReportTools.ShowReport("0123456789abcdef0123456789abcdef"));

            Assert.Equal("show_report", sent.Command);
            Assert.Equal("0123456789abcdef0123456789abcdef", sent.Json().Value<string>("report_id"));
        }

        [Fact]
        public async Task Lowercases_the_report_id_because_saved_ids_are_lowercase()
        {
            var sent = await Capture.Send(() => ReportTools.ShowReport("0123456789ABCDEF0123456789ABCDEF"));

            Assert.Equal("0123456789abcdef0123456789abcdef", sent.Json().Value<string>("report_id"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-report")]
        [InlineData("..\0123456789abcdef0123456789ab")]
        [InlineData("0123456789abcdef0123456789abcdef0")]
        public async Task Rejects_bad_report_ids_without_reaching_revit(string reportId)
        {
            var sent = false;
            string result = null;
            await Capture.WithSendOverride((c, p, t) => { sent = true; return Task.FromResult(new JObject()); },
                async () => result = await ReportTools.ShowReport(reportId));

            Assert.False(sent);
            Assert.StartsWith("Error: report_id must be", result);
        }
    }
}
