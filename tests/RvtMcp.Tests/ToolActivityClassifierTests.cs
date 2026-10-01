using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolActivityClassifierTests
    {
        [Theory]
        [InlineData("get_element_details", ToolActivityKind.Read)]
        [InlineData("list_rooms", ToolActivityKind.Read)]
        [InlineData("create_grid", ToolActivityKind.Write)]
        [InlineData("delete_element", ToolActivityKind.Write)]
        [InlineData("export_pdf", ToolActivityKind.Write)]
        [InlineData("load_selection", ToolActivityKind.Read)]
        [InlineData("clash_detection", ToolActivityKind.Read)]
        [InlineData("send_code_to_revit", ToolActivityKind.Write)]
        [InlineData("batch_execute", ToolActivityKind.Write)]
        [InlineData("run_baked_tool", ToolActivityKind.Write)]
        [InlineData("workflow_model_audit", ToolActivityKind.Read)]
        [InlineData("change_element_type", ToolActivityKind.Write)]
        [InlineData("select_elements", ToolActivityKind.Read)]
        public void Classify_KnownCommands_ReturnExpected(string commandName, ToolActivityKind expected)
        {
            Assert.Equal(expected, ToolActivityClassifier.Classify(commandName));
        }

        [Fact]
        public void Classify_AllRegisteredHandlerNames_DoNotThrow()
        {
            var handlersDir = Path.Combine(GetRepoRoot(), "src", "shared", "Handlers");
            var pattern = new Regex(@"public\s+string\s+Name\s*=>\s*""([^""]+)""", RegexOptions.Compiled);
            var count = 0;

            foreach (var file in Directory.EnumerateFiles(handlersDir, "*Handler.cs", SearchOption.TopDirectoryOnly))
            {
                var text = File.ReadAllText(file);
                foreach (Match match in pattern.Matches(text))
                {
                    var name = match.Groups[1].Value;
                    var kind = ToolActivityClassifier.Classify(name);
                    Assert.True(kind == ToolActivityKind.Read || kind == ToolActivityKind.Write);
                    count++;
                }
            }

            Assert.True(count > 50, "Expected to classify a substantial set of handler command names.");
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
