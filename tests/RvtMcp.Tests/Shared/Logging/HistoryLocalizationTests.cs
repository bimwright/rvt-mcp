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

        [Theory]
        [InlineData("compile_only", "Compile check")]
        [InlineData("module_compile", "Compile check")]
        [InlineData("script", "Script execute")]
        [InlineData("baked_script", "Baked script")]
        public void Script_receipt_summaries_refresh_language_without_replaying_inputs(string kind, string label)
        {
            var entry = new McpCallEntry { ToolName = "send_code_to_revit", Success = false, PreserveSummary = true,
                ParamsJson = "{\"private\":\"runtime-input-not-for-replay\"}", ErrorMessage = "TASK_RESULT_REQUIRED",
                Script = new Newtonsoft.Json.Linq.JObject { ["execution_kind"] = kind },
                Operation = new Newtonsoft.Json.Linq.JObject { ["outcome"] = "partial" } };
            McpSessionLog.RefreshSummary(entry);
            Assert.Contains(label, entry.Summary); Assert.Contains("native: partial", entry.Summary);
            Assert.Contains("TASK_RESULT_REQUIRED", entry.Summary);
            var en = EmbeddedCatalog.Load(typeof(HistoryLocalizationTests).Assembly, "en");
            var translated = new System.Collections.Generic.Dictionary<string,string>(en) {
                ["script.label.compile"] = "Biên dịch", ["script.label.execute"] = "Chạy script", ["script.label.baked"] = "Script đã lưu",
                ["script.native.partial"] = "Revit: một phần" };
            L.InitializeForTests(StringTable.Build("tt", en, translated, null));
            McpSessionLog.RefreshSummary(entry);
            Assert.Contains("Revit: một phần", entry.Summary); Assert.Contains("TASK_RESULT_REQUIRED", entry.Summary);
            Assert.DoesNotContain("runtime-input-not-for-replay", entry.Summary);
        }

        [Theory]
        [InlineData("TASK_FAILED", "rolled_back", "native: rolled_back")]
        [InlineData("TASK_RESULT_REQUIRED", "partial", "native: partial")]
        public void Script_toast_retains_failure_and_native_outcome_before_advisory_hints(string error, string native, string label)
        {
            var receipt = new Newtonsoft.Json.Linq.JObject { ["executed"] = true,
                ["_script"] = new Newtonsoft.Json.Linq.JObject { ["execution_kind"] = "script" },
                ["_operation"] = new Newtonsoft.Json.Linq.JObject { ["outcome"] = native },
                ["task_contract_hint"] = "advisory must not replace the error" };
            var toast = RvtMcp.Plugin.Views.Toast.ToastContentBuilder.BuildCompleted("send_code_to_revit", "{}", receipt.ToString(), false, error, 7, "Script");
            Assert.False(toast.Success); Assert.Contains(label, toast.Title); Assert.Contains(error, toast.Body);
            Assert.DoesNotContain("advisory must not replace", toast.Body);
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
