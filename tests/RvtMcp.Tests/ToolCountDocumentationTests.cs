using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolCountDocumentationTests
    {
        [Fact]
        public void Current_documentation_matches_golden_tool_counts()
        {
            var root = GetRepoRoot();
            var goldenRoot = Path.Combine(root, "tests", "RvtMcp.Tests", "Golden");
            var standard = ReadToolCount(Path.Combine(goldenRoot, "tools-list.json"));
            var adaptive = ReadToolCount(Path.Combine(goldenRoot, "tools-list-adaptive-bake.json"));

            foreach (var readmeName in new[] { "README.md", "README.vi.md", "README.zh-CN.md", "README.ja.md" })
            {
                var readme = File.ReadAllText(Path.Combine(root, readmeName));
                Assert.Contains("MCP-" + standard + "%20tools", readme);
                Assert.Contains("| `--toolsets all` | **" + standard + "**", readme);
                Assert.Contains("| `all` + adaptive bake | **" + adaptive + "**", readme);
            }

            var claude = File.ReadAllText(Path.Combine(root, "CLAUDE.md"));
            Assert.Contains(standard + " Revit tools with `--toolsets all` (" + adaptive + " with adaptive bake)", claude);

            var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
            Assert.Contains("`--toolsets all` **" + standard + "**, adaptive bake **" + adaptive + "**", changelog);

            var survey = File.ReadAllText(Path.Combine(root, "docs", "design", "oversized-response-survey.md"));
            Assert.Contains("**Inventory:** " + adaptive + " `[McpServerTool]` (" + standard + " surface", survey);
        }

        private static int ReadToolCount(string path)
        {
            return JObject.Parse(File.ReadAllText(path)).Value<int>("tool_count");
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
