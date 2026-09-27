using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class LintToolShellTests
    {
        private static readonly string[] LintToolsInClass =
        {
            "revit_analyze_view_naming_patterns",
            "revit_detect_firm_profile",
            "revit_get_model_warnings_summary",
            "revit_suggest_view_name_corrections"
        };

        [Fact]
        public void Every_lint_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(LintTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(LintToolsInClass, declared);
        }

        [Fact]
        public async Task Analyze_view_naming_patterns_sends_bounds()
        {
            var sent = await Capture.Send(() => LintTools.AnalyzeViewNamingPatterns(10, 5, 7));

            Assert.Equal("analyze_view_naming_patterns", sent.Command);
            var json = sent.Json();
            Assert.Equal(10, json.Value<int>("max_patterns"));
            Assert.Equal(5, json.Value<int>("start_outlier"));
            Assert.Equal(7, json.Value<int>("max_outliers"));
        }

        [Fact]
        public async Task Suggest_view_name_corrections_sends_profile()
        {
            var sent = await Capture.Send(() => LintTools.SuggestViewNameCorrections("kei"));

            Assert.Equal("suggest_view_name_corrections", sent.Command);
            Assert.Equal("kei", sent.Json().Value<string>("profile"));
        }

        [Fact]
        public async Task Detect_firm_profile_sends_no_payload()
        {
            var sent = await Capture.Send(() => LintTools.DetectFirmProfile());

            Assert.Equal("detect_firm_profile", sent.Command);
            Assert.Null(sent.Parameters);
        }

        [Fact]
        public async Task Get_model_warnings_summary_sends_bounds()
        {
            var sent = await Capture.Send(() => LintTools.GetModelWarningsSummary(false, 3, 50));

            Assert.Equal("get_model_warnings_summary", sent.Command);
            var json = sent.Json();
            Assert.False(json.Value<bool>("include_examples"));
            Assert.Equal(3, json.Value<int>("max_examples_per_type"));
            Assert.Equal(50, json.Value<int>("max_warning_types"));
        }
    }
}
