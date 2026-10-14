using RvtMcp.Plugin;
using Newtonsoft.Json;
using Xunit;

namespace RvtMcp.Tests
{
    public class McpResponsePrivacyTests
    {
        [Fact]
        public void RedactDataForResponse_RedactsSendCodeResultData()
        {
            var data = new
            {
                executed = true,
                result = @"C:\Users\Admin\Documents\Project A&B.rvt opened in Level 01"
            };

            var redacted = McpResponsePrivacy.RedactDataForResponse("send_code_to_revit", data);
            var serialized = JsonConvert.SerializeObject(redacted);

            Assert.DoesNotContain("Project A&B", serialized);
            Assert.DoesNotContain(@"C:\Users\Admin\Documents", serialized);
            Assert.DoesNotContain("Level 01", serialized);
            Assert.Contains("<project_file>", serialized);
        }

        [Fact]
        public void RedactDataForResponse_LeavesNonSendCodeDataUnchanged()
        {
            var data = new { result = "Level 01" };

            var redacted = McpResponsePrivacy.RedactDataForResponse("get_current_view_info", data);

            Assert.Same(data, redacted);
        }

        [Fact]
        public void RedactErrorForResponse_RedactsPathUrlsSecretsAndBimFiles()
        {
            var redacted = McpResponsePrivacy.RedactErrorForResponse(
                @"Runtime error: failed at C:\Users\Admin\Documents\Project A&B.rvt for https://example.com/client?token=sk-testsecret12345");

            Assert.DoesNotContain("Project A&B", redacted);
            Assert.DoesNotContain(@"C:\Users\Admin\Documents", redacted);
            Assert.DoesNotContain("https://example.com", redacted);
            Assert.DoesNotContain("sk-testsecret12345", redacted);
            Assert.Contains("<project_file>", redacted);
        }
    }
}
