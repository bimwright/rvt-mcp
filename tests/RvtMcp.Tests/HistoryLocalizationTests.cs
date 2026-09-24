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
