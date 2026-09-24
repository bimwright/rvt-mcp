using System;
using System.Collections.Generic;
using System.IO;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class RvtMcpConfigLocalizationTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "rvt-l10n-" + Guid.NewGuid().ToString("N"));

        private string Cfg => Path.Combine(_dir, "rvtmcp.config.json");

        public RvtMcpConfigLocalizationTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static Func<string, string> EnvLookup(Dictionary<string, string> map) =>
            name => map != null && map.TryGetValue(name, out var v) ? v : null;

        [Fact]
        public void UiLanguage_LoadsFromJson()
        {
            File.WriteAllText(Cfg, "{ \"uiLanguage\": \"ja\" }");
            var config = RvtMcpConfig.Load(null, Cfg, EnvLookup(null));
            Assert.Equal("ja", config.UiLanguage);
        }

        [Fact]
        public void UiLanguage_EnvOverridesJson()
        {
            // BIMWRIGHT_UI_LANGUAGE is a Revit.exe process env var (Windows user/machine);
            // the MCP client env block reaches the server, not the plugin.
            File.WriteAllText(Cfg, "{ \"uiLanguage\": \"ja\" }");
            var env = new Dictionary<string, string> { [RvtMcpConfig.EnvUiLanguage] = "de" };
            var config = RvtMcpConfig.Load(null, Cfg, EnvLookup(env));
            Assert.Equal("de", config.UiLanguage);
        }

        [Fact]
        public void UiLanguage_AbsentEverywhere_StaysNull()
        {
            var config = RvtMcpConfig.Load(null, Cfg, EnvLookup(null));
            Assert.Null(config.UiLanguage);
        }

        [Fact]
        public void SaveUiLanguage_RoundTripsAndPreservesOtherKeys()
        {
            File.WriteAllText(Cfg, "{ \"enableToast\": false }");
            RvtMcpConfig.SaveUiLanguage("de", Cfg);
            var config = RvtMcpConfig.Load(null, Cfg, EnvLookup(null));
            Assert.Equal("de", config.UiLanguage);
            Assert.False(config.EnableToastOrDefault);
        }

        [Fact]
        public void SaveUiLanguage_Auto_RoundTrips()
        {
            RvtMcpConfig.SaveUiLanguage("auto", Cfg);
            var config = RvtMcpConfig.Load(null, Cfg, EnvLookup(null));
            Assert.Equal("auto", config.UiLanguage);
        }
    }
}
