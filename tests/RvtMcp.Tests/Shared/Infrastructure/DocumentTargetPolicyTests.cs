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
        [InlineData("send_code_to_revit", "doc-a", null, "DOCUMENT_CHANGED")]
        [InlineData("send_code_to_revit", null, "doc-a", "DOCUMENT_UNCONFIRMED")]
        [InlineData("send_code_to_revit", null, null, null)]
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

        [Theory]
        [InlineData("create_level", null, "doc-a", false, "PAIRING_REQUIRED")]
        [InlineData("send_code_to_revit", null, "doc-a", false, "PAIRING_REQUIRED")]
        [InlineData("send_code_to_revit", null, null, false, "PAIRING_REQUIRED")]
        [InlineData("create_level", null, "doc-a", true, "DOCUMENT_UNCONFIRMED")]
        [InlineData("create_level", "doc-a", "doc-b", false, "DOCUMENT_CHANGED")]
        [InlineData("create_level", "doc-a", "doc-a", false, null)]
        [InlineData("get_current_view_info", null, "doc-a", false, null)]
        [InlineData("open_model", null, null, false, null)]
        public void Gateway_without_document_protocol_is_told_to_update_instead_of_to_verify(
            string command, string expected, string actual, bool gatewayConfirmsDocuments, string error)
        {
            var rejection = ToolReadPolicy.DocumentRejection(command, expected, actual, gatewayConfirmsDocuments);
            if (error == null) Assert.Null(rejection);
            else Assert.StartsWith(error + ":", rejection);
            if (error == "PAIRING_REQUIRED") Assert.DoesNotContain("revit_switch_target(verify=true) before", rejection);
        }
    }
}
