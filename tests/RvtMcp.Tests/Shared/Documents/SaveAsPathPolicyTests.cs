using RvtMcp.Plugin;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    /// <summary>
    /// Save As rules behind revit_save_document(save_as_path). Path cases from PR #29.
    /// </summary>
    public class SaveAsPathPolicyTests
    {
        [Theory]
        [InlineData("unsaved")]
        [InlineData("template")]
        public void Documents_without_a_project_file_may_save_as(string mode)
        {
            Assert.Null(SaveAsPathPolicy.CheckMode(mode));
        }

        [Theory]
        [InlineData("local_file")]
        [InlineData("family")]
        [InlineData("workshared_local")]
        [InlineData("workshared_central")]
        [InlineData("cloud")]
        [InlineData(null)]
        public void Saved_or_unsupported_documents_may_not_save_as(string mode)
        {
            Assert.Equal("SAVE_AS_NOT_ALLOWED", SaveAsPathPolicy.CheckMode(mode));
        }

        [Theory]
        [InlineData(@"C:\Projects\Office.rvt")]
        [InlineData("C:/Projects/Office.RVT")]
        public void Local_absolute_rvt_path_is_accepted(string path)
        {
            Assert.Null(SaveAsPathPolicy.CheckPath(path));
        }

        [Theory]
        [InlineData(@"\\server\share\Office.rvt", "network")]
        [InlineData("//server/share/Office.rvt", "network")]
        [InlineData(@"\Projects\Office.rvt", "drive letter")]
        [InlineData("C:Office.rvt", "drive letter")]
        [InlineData(@"Projects\Office.rvt", "drive letter")]
        [InlineData(@"C:\Projects\Office.rte", "rvt extension")]
        [InlineData("", "empty")]
        public void Other_paths_are_refused_with_a_reason(string path, string reason)
        {
            Assert.Contains(reason, SaveAsPathPolicy.CheckPath(path));
        }

        [Theory]
        [InlineData(@"\\server\share\Office.rvt")]
        [InlineData("C:Office.rvt")]
        [InlineData(@"C:\Projects\Office.rte")]
        [InlineData("")]
        public void Refusals_survive_redaction_unchanged(string path)
        {
            // Error text goes through BakeRedactor, which replaces anything that looks like a path or a
            // .rvt file name, so a refusal must not quote either or the agent gets a garbled message.
            var reason = SaveAsPathPolicy.CheckPath(path);
            Assert.Equal(reason, BakeRedactor.RedactForBake(reason));
        }
    }
}
