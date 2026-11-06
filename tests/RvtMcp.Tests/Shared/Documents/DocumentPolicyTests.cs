using System;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class DocumentPolicyTests
    {
        [Theory]
        [InlineData("local_file")]
        [InlineData("family")]
        public void Local_documents_can_save_and_manage_checkpoints(string mode)
        {
            var policy = DocumentPolicy.ForMode(mode);
            Assert.Equal(mode, policy.Mode);
            Assert.Equal(new[] { "revit_get_document_state", "revit_save_document", "revit_save_checkpoint", "revit_manage_checkpoints" }, policy.Allowed);
            Assert.Contains(policy.Rules, r => r.Contains("at least 20"));
            Assert.Contains(policy.Rules, r => r.Contains("known") && r.Contains("higher"));
        }

        [Fact]
        public void Workshared_local_may_save_but_may_not_checkpoint()
        {
            var policy = DocumentPolicy.ForMode("workshared_local");
            Assert.Equal(new[] { "revit_get_document_state", "revit_save_document" }, policy.Allowed);
            Assert.Contains(policy.Rules, r => r.Contains("not synced"));
            Assert.Contains(policy.Rules, r => r.Contains("user") && r.Contains("unsynced"));
        }

        [Theory]
        [InlineData("workshared_central")]
        [InlineData("cloud")]
        public void Unsupported_save_modes_allow_state_only(string mode)
        {
            Assert.Equal(new[] { "revit_get_document_state" }, DocumentPolicy.ForMode(mode).Allowed);
        }

        [Theory]
        [InlineData("unsaved")]
        [InlineData("template")]
        public void Documents_without_a_project_file_may_only_save_as_to_a_user_path(string mode)
        {
            var policy = DocumentPolicy.ForMode(mode);
            Assert.Equal(new[] { "revit_get_document_state", "revit_save_document" }, policy.Allowed);
            Assert.Contains(policy.Rules, r => r.Contains("save_as_path") && r.Contains("never choose the path"));
        }

        [Fact]
        public void Unknown_mode_does_not_grant_tools()
        {
            Assert.Throws<ArgumentException>(() => DocumentPolicy.ForMode("unknown"));
        }
    }
}
