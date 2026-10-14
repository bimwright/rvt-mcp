using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class DocumentTargetPolicyTests
    {
        [Theory]
        [InlineData("create_level", null, "doc-a", "DOCUMENT_UNCONFIRMED")]
        [InlineData("create_level", "doc-a", "doc-b", "DOCUMENT_CHANGED")]
        [InlineData("create_level", "doc-a", null, "DOCUMENT_CHANGED")]
        [InlineData("send_code_to_revit", "doc-a", "doc-b", "DOCUMENT_CHANGED")]
        [InlineData("batch_execute", "doc-a", "doc-b", "DOCUMENT_CHANGED")]
        [InlineData("create_level", "doc-a", "doc-a", null)]
        [InlineData("get_current_view_info", null, "doc-b", null)]
        [InlineData("open_model", null, null, null)]
        public void Queued_request_checks_actual_document_before_execution(string command, string expected, string actual, string error)
        {
            var rejection = ToolReadPolicy.DocumentRejection(command, expected, actual);
            if (error == null) Assert.Null(rejection);
            else Assert.StartsWith(error + ":", rejection);
        }
    }
}
