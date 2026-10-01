using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolPermissionsDocumentationTests
    {
        [Theory]
        [InlineData("README.md")]
        [InlineData("README.vi.md")]
        [InlineData("README.ja.md")]
        [InlineData("README.zh-CN.md")]
        public void Permissions_allow_list_matches_read_only_annotations(string file)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.Contains("Permissions & auto mode", text);
            Assert.Contains("`mcp__rvt-mcp__*`", text);
            var start = text.IndexOf("<!-- BEGIN READ_ONLY_ALLOWLIST -->");
            var end = text.IndexOf("<!-- END READ_ONLY_ALLOWLIST -->");
            Assert.True(start >= 0 && end > start);
            var block = text.Substring(start, end - start);
            var actual = Regex.Matches(block, "mcp__rvt-mcp__revit_([a-z0-9_]+)")
                .Select(match => match.Groups[1].Value).OrderBy(n => n);
            Assert.Equal(ToolReadPolicy.ReadOnlyCommands.OrderBy(n => n), actual);
        }

        private static string RepoRoot([CallerFilePath] string source = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "..", ".."));
    }
}
