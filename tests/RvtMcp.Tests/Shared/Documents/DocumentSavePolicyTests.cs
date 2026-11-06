using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class DocumentSavePolicyTests
    {
        [Theory]
        [InlineData("cloud", false, "CLOUD_MODEL_UNSUPPORTED")]
        [InlineData("unsaved", false, "DOCUMENT_NEVER_SAVED")]
        [InlineData("template", false, "TEMPLATE_SAVE_AS_REQUIRED")]
        [InlineData("template", true, "TEMPLATE_SAVE_AS_REQUIRED")]
        [InlineData("workshared_central", false, "CENTRAL_OPEN_DIRECTLY")]
        [InlineData("workshared_local", true, "CHECKPOINT_WORKSHARED_UNSUPPORTED")]
        [InlineData("workshared_central", true, "CHECKPOINT_WORKSHARED_UNSUPPORTED")]
        public void Mode_is_checked_before_disk_or_save_state(string mode, bool checkpoint, string expected)
        {
            Assert.Equal(expected, DocumentSavePolicy.Check(mode, false, "changed_on_disk", false, true, false, true, checkpoint));
        }

        [Fact]
        public void Missing_file_and_external_change_are_checked_before_no_changes()
        {
            Assert.Equal("FILE_MISSING_ON_DISK", DocumentSavePolicy.Check("local_file", false, "changed_on_disk", false, true, false, true, false));
            Assert.Equal("FILE_CHANGED_ON_DISK", DocumentSavePolicy.Check("local_file", true, "changed_on_disk", false, true, false, true, false));
        }

        [Fact]
        public void Unmodified_save_stops_before_upgrade_and_read_only_checks()
        {
            Assert.Equal("no_changes", DocumentSavePolicy.Check("local_file", true, "in_sync", false, true, false, true, false));
        }

        [Fact]
        public void Upgrade_requires_approval_before_read_only_is_checked()
        {
            Assert.Equal("FORMAT_UPGRADE_NEEDS_USER", DocumentSavePolicy.Check("family", true, "in_sync", true, true, false, true, false));
            Assert.Equal("FILE_READ_ONLY", DocumentSavePolicy.Check("family", true, "in_sync", true, true, true, true, false));
        }

        [Fact]
        public void Unmodified_checkpoint_can_copy_read_only_or_old_format_source()
        {
            Assert.Null(DocumentSavePolicy.Check("local_file", true, "in_sync", false, true, false, true, true));
        }

        [Theory]
        [InlineData("local_file")]
        [InlineData("family")]
        [InlineData("workshared_local")]
        public void Modified_writable_documents_can_proceed(string mode)
        {
            Assert.Null(DocumentSavePolicy.Check(mode, true, "in_sync", true, false, false, false, false));
        }
    }
}
