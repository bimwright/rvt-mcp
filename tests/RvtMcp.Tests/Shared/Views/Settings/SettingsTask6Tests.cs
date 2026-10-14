using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class SettingsTask6Tests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "rvtmcp-settings-task6-" + Guid.NewGuid().ToString("N") + ".json");

        [Fact]
        public void Apply_PrivacyCache_IsStagedAndPreservesUnrelatedKeys()
        {
            RvtMcpConfig.SaveEnableToast(true, _path);
            var result = RvtMcpConfig.TryApplySettings(new RvtMcpConfig.SettingsPatch
            {
                CacheSendCodeBodies = true,
            }, _path);

            Assert.Contains("cacheSendCodeBodies", result.Applied);
            Assert.Empty(result.Failed);
            var root = JObject.Parse(File.ReadAllText(_path));
            Assert.True(root.Value<bool>("cacheSendCodeBodies"));
            Assert.True(root.Value<bool>("enableToast"));
        }

        [Fact]
        public void Apply_UnrelatedPrivacyKey_DoesNotRenewActiveJournal()
        {
            var until = DateTimeOffset.UtcNow.AddHours(2);
            RvtMcpConfig.SavePersistSendCodeBodies(true, until, _path);
            var before = JObject.Parse(File.ReadAllText(_path)).Value<string>("persistSendCodeBodiesUntil");

            var result = RvtMcpConfig.TryApplySettings(new RvtMcpConfig.SettingsPatch
            {
                CacheSendCodeBodies = true,
            }, _path);

            Assert.Empty(result.Failed);
            var after = JObject.Parse(File.ReadAllText(_path));
            Assert.Equal(before, after.Value<string>("persistSendCodeBodiesUntil"));
            Assert.True(after.Value<bool>("cacheSendCodeBodies"));
        }

        public void Dispose()
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }
}
