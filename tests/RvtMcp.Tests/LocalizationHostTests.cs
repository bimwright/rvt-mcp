using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("L10n")]
    public class LocalizationHostTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "rvt-l10n-host-" + Guid.NewGuid().ToString("N"));

        public LocalizationHostTests()
        {
            Directory.CreateDirectory(_dir);
            L.ResetForTests();
            L.ForceSyncBuilds = true;
        }

        public void Dispose()
        {
            L.ResetForTests();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static readonly IReadOnlyDictionary<string, string> En =
            new Dictionary<string, string>
            {
                ["greet"] = "Hello",
                ["counted"] = "{count:n} items",
                ["security.warn"] = "Careful: {detail}",
            };

        private static readonly IReadOnlyDictionary<string, string> De =
            new Dictionary<string, string>
            {
                ["greet"] = "Hallo",
                ["security.warn"] = "Vorsicht: {detail}",
            };

        private static IReadOnlyDictionary<string, string> Loader(string locale)
        {
            if (locale == "en") return En;
            if (locale == "de") return De;
            return null;
        }

        // Injection-only watcher (no real FileSystemWatcher) — deterministic tests.
        private LocalizationHost NewHost(bool startWatcher = true)
        {
            var h = new LocalizationHost(_dir, Loader, TimeSpan.Zero, watchFilesystem: false);
            h.Initialize("German", "auto", startWatcher);
            return h;
        }

        private void WriteOverride(string locale, string json)
            => File.WriteAllText(Path.Combine(_dir, "strings." + locale + ".json"), json);

        [Fact]
        public void Initialize_BuildsTable_FromEmbeddedPlusOverrides()
        {
            WriteOverride("de", "{ \"greet\": \"Hallo-override\" }");
            using (NewHost())
            {
                Assert.Equal("de", L.Locale);
                Assert.Equal("Hallo-override", L.T("greet"));
                Assert.Equal("42 items", L.T("counted", ("count", 42))); // en fallback
            }
        }

        [Fact]
        public void Initialize_EnOverrideFile_AppliesToFallbackLayer()
        {
            WriteOverride("en", "{ \"counted\": \"{count:n} things\" }");
            using (NewHost())
            {
                Assert.Equal("42 things", L.T("counted", ("count", 42)));
            }
        }

        [Fact]
        public void Swap_WritesActiveSidecar()
        {
            using (NewHost())
            {
                var path = LocalizationReport.ActivePath(_dir, "de");
                Assert.True(File.Exists(path));
                var active = JObject.Parse(File.ReadAllText(path));
                Assert.Equal("Hallo", active["strings"].Value<string>("greet"));
                Assert.Contains("security.warn",
                    active["locked"].ToObject<List<string>>());
            }
        }

        [Fact]
        public void Swap_WritesReport_ForRejectedOverride()
        {
            WriteOverride("de", "{ \"greet\": \"Hallo\", \"security.warn\": \"x {detail}\", \"zzz\": \"y\" }");
            using (NewHost())
            {
                var report = JObject.Parse(
                    File.ReadAllText(LocalizationReport.ReportPath(_dir, "de")));
                var reasons = report["rejected"].ToObject<List<Dictionary<string, string>>>();
                Assert.Equal(2, reasons.Count);
            }
        }

        [Fact]
        public void MissingEmbeddedLocale_ReportListsMissing()
        {
            var host = new LocalizationHost(_dir, Loader, TimeSpan.Zero);
            using (host)
            {
                host.Initialize("German", "fr", startWatcher: false);   // explicit fr — no fr catalog
                Assert.Equal("fr", L.Locale);
                Assert.Equal("Hello", L.T("greet"));                    // en fallback
                var report = JObject.Parse(
                    File.ReadAllText(LocalizationReport.ReportPath(_dir, "fr")));
                Assert.Equal(En.Count, report["missing"].ToObject<JArray>().Count);
            }
        }

        [Fact]
        public void InvalidOverride_OnReload_RetainsPreviousTable()
        {
            WriteOverride("de", "{ \"greet\": \"Custom\" }");
            using (var host = NewHost())
            {
                Assert.Equal("Custom", L.T("greet"));

                WriteOverride("de", "{ half-written");   // invalid_json
                var vBefore = L.Version;
                host.NotifyOverrideFileEvent(Path.Combine(_dir, "strings.de.json"));
                Assert.True(L.Version > vBefore,
                    "reload did not run; skip=" + L.LastSkipReason);

                Assert.Equal("Custom", L.T("greet"));   // previous table retained
                var report = JObject.Parse(
                    File.ReadAllText(LocalizationReport.ReportPath(_dir, "de")));
                Assert.Equal("invalid_json", report.Value<string>("fileError"));
            }
        }

        [Fact]
        public void ValidEdit_OnReload_TakesEffect()
        {
            WriteOverride("de", "{ \"greet\": \"V1\" }");
            using (var host = NewHost())
            {
                Assert.Equal("V1", L.T("greet"));
                WriteOverride("de", "{ \"greet\": \"V2\" }");
                host.NotifyOverrideFileEvent(Path.Combine(_dir, "strings.de.json"));
                Assert.Equal("V2", L.T("greet"));
            }
        }

        [Fact]
        public void IrrelevantFile_OnReload_NoChange()
        {
            using (var host = NewHost())
            {
                var v = L.Version;
                host.NotifyOverrideFileEvent(Path.Combine(_dir, "strings.fr.json"));
                Assert.Equal(v, L.Version);
            }
        }
    }
}
