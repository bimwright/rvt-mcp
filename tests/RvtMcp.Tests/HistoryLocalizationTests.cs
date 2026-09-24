using System;
using Newtonsoft.Json;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>History summaries recompute through SummaryGenerator on language
    /// change; security notes stay verbatim-safe (locked keys).</summary>
    [Collection("L10n")]
    public class HistoryLocalizationTests : IDisposable
    {
        public HistoryLocalizationTests()
        {
            L.ResetForTests();
            var en = EmbeddedCatalog.Load(typeof(HistoryLocalizationTests).Assembly, "en");
            L.InitializeForTests(StringTable.Build("en", en, null, null));
        }

        public void Dispose() => L.ResetForTests();

        [Fact]
        public void Generate_uses_catalog_strings_in_english()
        {
            Assert.Equal("Elements selected: 3",
                SummaryGenerator.Generate("get_selected_elements", null, "{\"count\":3}", true, null));
            Assert.Equal("Items: 12",
                SummaryGenerator.Generate("list_levels", null, "{\"count\":12}", true, null));
            Assert.Equal("OK",
                SummaryGenerator.Generate("list_levels", null, null, true, null));
            Assert.Equal("boom",
                SummaryGenerator.Generate("x", null, null, false, "boom"));
        }

        [Fact]
        public void Generate_redacted_send_code_params_emit_security_note()
        {
            var parms = JsonConvert.SerializeObject(new { code_hash = "abc123", code_length = 42 });
            var summary = SummaryGenerator.Generate("send_code_to_revit", parms, null, true, null);
            Assert.Equal("send_code_to_revit body redacted; code_hash=abc123; code_length=42", summary);
        }

        [Fact]
        public void Generate_failed_call_returns_error_before_parsing_params()
        {
            // Truncated (string literal) or non-object params must not swallow the
            // real error message — success is checked before any JSON parsing.
            Assert.Equal("param is required",
                SummaryGenerator.Generate("create_grid", "\"{\\\"code\\\": \\\"abc", null,
                    false, "param is required"));
            Assert.Equal("boom",
                SummaryGenerator.Generate("x", "[1,2,3]", null, false, "boom"));
            Assert.Equal("Failed",
                SummaryGenerator.Generate("x", "{not an object", null, false, null));
        }

        [Fact]
        public void Generate_security_note_length_formats_invariant_under_any_culture()
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                var parms = JsonConvert.SerializeObject(new { code_hash = "abc", code_length = 1234 });
                var summary = SummaryGenerator.Generate("send_code_to_revit", parms, null, true, null);
                // key=value security notes stay invariant — never "1.234".
                Assert.Equal("send_code_to_revit body redacted; code_hash=abc; code_length=1234",
                    summary);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
        }

        [Fact]
        public void RefreshSummary_keeps_preserved_summaries()
        {
            // Privacy-redacted result can't regenerate "Walls: 42" — PreserveSummary
            // entries (set by McpSessionLog / McpEventHandler) are never recomputed.
            var entry = new McpCallEntry
            {
                ToolName = "send_code_to_revit",
                ResultJson = "{\"result\":\"<result_1>\"}",
                ParamsJson = "{\"code\":\"print(1)\"}",
                Success = true,
                Summary = "Walls: 42",
                PreserveSummary = true
            };
            McpSessionLog.RefreshSummary(entry);
            Assert.Equal("Walls: 42", entry.Summary);
        }

        [Fact]
        public void Unknown_command_summary_key_is_localized()
        {
            Assert.Equal("Unknown: create_foo",
                L.T("history.summary.unknown", ("tool", "create_foo")));
        }

        [Fact]
        public void RefreshSummary_recomputes_in_new_language_and_keeps_truncated()
        {
            var en = EmbeddedCatalog.Load(typeof(HistoryLocalizationTests).Assembly, "en");
            var tt = new System.Collections.Generic.Dictionary<string, string>(en)
            {
                ["history.summary.selected"] = "Đã chọn: {count:n} phần tử",
                ["history.summary.ok"] = "XONG",
            };

            var entry = new McpCallEntry
            {
                ToolName = "get_selected_elements",
                ResultJson = "{\"count\":2}",
                Success = true,
                Summary = "Elements selected: 2"
            };
            var truncated = new McpCallEntry
            {
                ToolName = "x", Success = true, ParamsTruncated = true,
                ParamsJson = "{\"big\":", Summary = "kept verbatim"
            };

            L.InitializeForTests(StringTable.Build("tt", en, tt, null));
            McpSessionLog.RefreshSummary(entry);
            McpSessionLog.RefreshSummary(truncated);

            Assert.Equal("Đã chọn: 2 phần tử", entry.Summary);
            Assert.Equal("kept verbatim", truncated.Summary);
        }
    }
}
