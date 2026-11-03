using RvtMcp.Plugin;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Path rules behind revit_save_model. Runs without Revit because SaveModelPolicy has no API references.
    /// </summary>
    public class SaveModelPolicyTests
    {
        [Theory]
        [InlineData(@"C:\Projects\Office.rvt")]
        [InlineData("C:/Projects/Office.RVT")]
        public void Local_absolute_rvt_path_is_accepted(string path)
        {
            Assert.Null(SaveModelPolicy.CheckSaveAsPath(path));
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
            Assert.Contains(reason, SaveModelPolicy.CheckSaveAsPath(path));
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
            var reason = SaveModelPolicy.CheckSaveAsPath(path);
            Assert.Equal(reason, BakeRedactor.RedactForBake(reason));
        }

        [Theory]
        [InlineData("", true)]
        [InlineData(@"C:\ProgramData\Autodesk\RVT 2027\Templates\English\DefaultMetric.rte", true)]
        [InlineData(@"C:\Projects\Office.rvt", false)]
        public void Template_or_unsaved_document_needs_save_as_path(string currentPath, bool needed)
        {
            Assert.Equal(needed, SaveModelPolicy.NeedsSaveAsPath(currentPath));
        }
    }
}
