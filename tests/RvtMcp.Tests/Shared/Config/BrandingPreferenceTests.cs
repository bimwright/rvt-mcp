using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class BrandingPreferenceTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rvtmcp-branding-" + Guid.NewGuid().ToString("N"));
        private string ConfigPath => Path.Combine(_directory, "rvtmcp.config.json");

        [Fact]
        public void Branding_is_off_when_unset_including_existing_configs()
        {
            Assert.False(new RvtMcpConfig().ShowBrandingOrDefault);
            Directory.CreateDirectory(_directory);
            File.WriteAllText(ConfigPath, "{\"enableToast\":true}");
            Assert.False(RvtMcpConfig.LoadFromJsonFile(ConfigPath).ShowBrandingOrDefault);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Saving_branding_survives_a_fresh_config_load(bool show)
        {
            Assert.True(RvtMcpConfig.TrySaveShowBranding(show, out var error, ConfigPath), error);
            Assert.Null(error);
            Assert.Equal(show, RvtMcpConfig.LoadFromJsonFile(ConfigPath).ShowBrandingOrDefault);
            Assert.Equal(show, JObject.Parse(File.ReadAllText(ConfigPath)).Value<bool>("showBranding"));
        }

        [Fact]
        public void Toggling_branding_preserves_unrelated_preferences_and_journal_expiry()
        {
            Directory.CreateDirectory(_directory);
            var original = JObject.Parse(@"{""enableToast"":false,""toastIdleSeconds"":60,""toolsets"":[""all""],
                ""persistSendCodeBodies"":true,""persistSendCodeBodiesUntil"":""2099-01-01T00:00:00Z"",
                ""custom"":{""keep"":42}}");
            File.WriteAllText(ConfigPath, original.ToString());

            foreach (var show in new[] { true, false })
            {
                Assert.True(RvtMcpConfig.TrySaveShowBranding(show, out var error, ConfigPath), error);
                var saved = JObject.Parse(File.ReadAllText(ConfigPath));
                Assert.Equal(show, saved.Value<bool>("showBranding"));
                saved.Remove("showBranding");
                Assert.True(JToken.DeepEquals(original, saved));
            }
        }

        [Fact]
        public void Saving_branding_reports_invalid_config_without_overwriting_it()
        {
            Directory.CreateDirectory(_directory);
            const string original = "{ invalid json";
            File.WriteAllText(ConfigPath, original);

            Assert.False(RvtMcpConfig.TrySaveShowBranding(true, out var error, ConfigPath));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(original, File.ReadAllText(ConfigPath));
        }

        [Fact]
        public void Saving_branding_reports_an_unwritable_destination()
        {
            Directory.CreateDirectory(_directory);
            Directory.CreateDirectory(ConfigPath);
            Assert.False(RvtMcpConfig.TrySaveShowBranding(true, out var error, ConfigPath));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.True(Directory.Exists(ConfigPath));
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
