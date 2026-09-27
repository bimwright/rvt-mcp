using System;
using System.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class RecentFileListTests
    {
        [Fact]
        public void Parse_reads_recent_file_section_in_index_order_and_skips_gaps()
        {
            var ini = @"
[Directories]
ProjectPath=D:\Projects
[Recent File List]
File1=D:\Models\newest.rvt
File2=
File3=D:\Models\older.rfa
; File4=D:\Models\commented.rvt
File8=D:\Models\gap.rte
[Recent Workset List]
File1=should-not-appear.rvt
";
            var files = RecentFileList.Parse(ini);

            Assert.Equal(new[] { 1, 3, 8 }, files.Select(file => file.Index).ToArray());
            Assert.Equal(@"D:\Models\newest.rvt", files[0].Path);
            Assert.Equal(@"D:\Models\older.rfa", files[1].Path);
            Assert.Equal(@"D:\Models\gap.rte", files[2].Path);
        }

        [Fact]
        public void Parse_keeps_the_fifty_most_recent_entries()
        {
            var lines = Enumerable.Range(1, 51).Select(i => "File" + i + "=D:\\Models\\" + i + ".rvt");
            var ini = "[Recent File List]\n" + string.Join("\n", lines);

            var files = RecentFileList.Parse(ini);

            Assert.Equal(50, files.Count);
            Assert.Equal(1, files[0].Index);
            Assert.Equal(50, files[49].Index);
        }

        [Fact]
        public void IniPath_uses_the_revit_year_profile()
        {
            var path = RecentFileList.IniPath("2022");

            Assert.EndsWith(
                System.IO.Path.Combine("Autodesk", "Revit", "Autodesk Revit 2022", "Revit.ini"),
                path);
        }

        [Theory]
        [InlineData("")]
        [InlineData("R22")]
        [InlineData("20222")]
        public void IniPath_rejects_a_year_that_is_not_four_digits(string year)
        {
            Assert.Throws<ArgumentException>(() => RecentFileList.IniPath(year));
        }
    }

    public class NoDocumentGuidanceTests
    {
        [Fact]
        public void Exact_handler_error_tells_the_agent_to_ask_which_recent_file_to_open()
        {
            var message = NoDocumentGuidance.ForAgent(NoDocumentGuidance.HandlerError);

            Assert.Equal(NoDocumentGuidance.AgentError, message);
            Assert.Contains("revit_list_recent_models", message);
            Assert.Contains("ask the user", message);
            Assert.Contains("revit_open_model", message);
        }

        [Fact]
        public void Other_errors_are_unchanged()
        {
            Assert.Equal("No active view.", NoDocumentGuidance.ForAgent("No active view."));
            Assert.Null(NoDocumentGuidance.ForAgent(null));
        }
    }
}
