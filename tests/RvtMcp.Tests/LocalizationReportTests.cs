using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    public class LocalizationReportTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "rvt-l10n-rep-" + Guid.NewGuid().ToString("N"));

        public LocalizationReportTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static Dictionary<string, string> En() => new Dictionary<string, string>
        {
            ["a"] = "A",
            ["b"] = "B {n}",
            ["security.warn"] = "Careful",
        };

        private static StringTable Table(string locale,
            IReadOnlyDictionary<string, string> embedded,
            IReadOnlyDictionary<string, string> overrides = null)
            => StringTable.Build(locale, En(), embedded, overrides);

        [Fact]
        public void Report_WritesMissingAndRejected()
        {
            var de = new Dictionary<string, string> { ["a"] = "A-de", ["security.warn"] = "Vorsicht" };   // only "b" missing
            var validation = OverrideValidator.ValidateJson(
                "{ \"security.warn\": \"x\", \"typo\": \"y\" }", En());
            var table = Table("de", de, validation.Accepted);

            LocalizationReport.WriteAll(_dir, "de", En(), de, table, validation);

            var report = JObject.Parse(
                File.ReadAllText(LocalizationReport.ReportPath(_dir, "de")));
            Assert.Equal("de", report.Value<string>("locale"));
            Assert.Single(report["missing"]);
            Assert.Equal("b", report["missing"][0].Value<string>("key"));
            Assert.Equal(2, report["rejected"].ToObject<JArray>().Count);
        }

        [Fact]
        public void Report_FileError_IsReported()
        {
            var validation = OverrideValidator.ValidateJson("{ bad", En());
            var table = Table("de", En());
            LocalizationReport.WriteAll(_dir, "de", En(), En(), table, validation);
            var report = JObject.Parse(
                File.ReadAllText(LocalizationReport.ReportPath(_dir, "de")));
            Assert.Equal("invalid_json", report.Value<string>("fileError"));
        }

        [Fact]
        public void Report_DeletedWhenNothingToReport()
        {
            var path = LocalizationReport.ReportPath(_dir, "de");
            File.WriteAllText(path, "{ stale }");
            var de = En();   // complete embedded catalog
            var table = Table("de", de);
            LocalizationReport.WriteAll(_dir, "de", En(), de, table, new OverrideValidator.Result());
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Active_AlwaysWritten_WithEffectiveAndLocked()
        {
            var de = new Dictionary<string, string> { ["a"] = "A-de" };
            var table = Table("de", de,
                new Dictionary<string, string> { ["b"] = "B-override {n}" });
            LocalizationReport.WriteAll(_dir, "de", En(), de, table, null);

            var active = JObject.Parse(
                File.ReadAllText(LocalizationReport.ActivePath(_dir, "de")));
            var strings = active["strings"].ToObject<Dictionary<string, string>>();
            Assert.Equal("A-de", strings["a"]);            // embedded
            Assert.Equal("B-override {n}", strings["b"]);  // override wins
            Assert.Equal("Careful", strings["security.warn"]); // en fallback
            var locked = active["locked"].ToObject<List<string>>();
            Assert.Equal(new List<string> { "security.warn" }, locked);
        }
    }
}
