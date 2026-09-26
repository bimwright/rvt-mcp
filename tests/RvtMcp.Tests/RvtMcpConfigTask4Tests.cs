using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class RvtMcpConfigTask4Tests
    {
        private static string NewConfigPath()
        {
            return Path.Combine(Path.GetTempPath(), "rvtmcp-task4-" + Path.GetRandomFileName() + ".json");
        }

        private static Func<string, string> EnvLookup(Dictionary<string, string> values)
        {
            return name => values.TryGetValue(name, out var value) ? value : null;
        }

        private static JObject ParseRaw(string path)
        {
            using (var textReader = new StringReader(File.ReadAllText(path)))
            using (var jsonReader = new JsonTextReader(textReader)
            {
                DateParseHandling = DateParseHandling.None,
            })
            {
                return (JObject)JToken.ReadFrom(jsonReader);
            }
        }

        [Fact]
        public void LoadReadOnly_does_not_expire_or_write_back_config()
        {
            var path = NewConfigPath();
            try
            {
                File.WriteAllText(path, "{\"target\":\"2024\",\"persistSendCodeBodies\":true,\"persistSendCodeBodiesUntil\":\"2020-01-01T00:00:00Z\",\"persistSendCodeBodiesHours\":12}");
                var before = File.ReadAllBytes(path);

                var config = RvtMcpConfig.LoadReadOnly(path, EnvLookup(new Dictionary<string, string>
                {
                    [RvtMcpConfig.EnvTarget] = "2027",
                    [RvtMcpConfig.EnvPersistSendCodeBodies] = "true",
                    [RvtMcpConfig.EnvPersistSendCodeBodiesTtl] = "1h",
                }));

                Assert.Equal("2027", config.Target); // env overlay is in memory only
                Assert.True(config.PersistSendCodeBodies);
                Assert.Equal(12, config.PersistSendCodeBodiesHoursOrDefault);
                Assert.Equal(before, File.ReadAllBytes(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void TrySave_uses_atomic_replace_and_preserves_unknown_keys()
        {
            var path = NewConfigPath();
            try
            {
                File.WriteAllText(path, "{\"target\":\"2026\",\"ownerExtension\":{\"keep\":true}}");
                string error;
                Assert.True(RvtMcpConfig.TrySaveToastIdleSeconds(30, out error, path), error);

                var root = JObject.Parse(File.ReadAllText(path));
                Assert.Equal(30, (int)root["toastIdleSeconds"]);
                Assert.Equal("2026", (string)root["target"]);
                Assert.True((bool)root["ownerExtension"]["keep"]);
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*.tmp"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void TryApplySettings_merges_external_edits_and_applies_ttl_rules()
        {
            var path = NewConfigPath();
            try
            {
                var initialUntil = DateTimeOffset.UtcNow.AddHours(1);
                File.WriteAllText(path, "{\"readOnly\":false,\"persistSendCodeBodies\":true,\"persistSendCodeBodiesHours\":4,\"persistSendCodeBodiesUntil\":\""
                    + initialUntil.ToString("O") + "\"}");

                // Simulate an edit made by another process while Settings was open.
                File.WriteAllText(path, "{\"readOnly\":true,\"externalEdit\":\"kept\",\"persistSendCodeBodies\":true,\"persistSendCodeBodiesHours\":4,\"persistSendCodeBodiesUntil\":\""
                    + initialUntil.ToString("O") + "\"}");
                var idleResult = RvtMcpConfig.TryApplySettings(
                    new RvtMcpConfig.SettingsPatch { ToastIdleSeconds = 60 }, path);
                Assert.Contains("toastIdleSeconds", idleResult.Applied);

                var afterIdle = ParseRaw(path);
                Assert.True((bool)afterIdle["readOnly"]);
                Assert.Equal("kept", (string)afterIdle["externalEdit"]);
                Assert.InRange(
                    DateTimeOffset.Parse((string)afterIdle["persistSendCodeBodiesUntil"]).ToUniversalTime() - initialUntil.ToUniversalTime(),
                    TimeSpan.FromSeconds(-1),
                    TimeSpan.FromSeconds(1));

                var durationResult = RvtMcpConfig.TryApplySettings(
                    new RvtMcpConfig.SettingsPatch { PersistSendCodeBodiesHours = 12 }, path);
                Assert.Contains("persistSendCodeBodiesHours", durationResult.Applied);
                var afterDuration = ParseRaw(path);
                var durationUntil = DateTimeOffset.Parse((string)afterDuration["persistSendCodeBodiesUntil"]);
                Assert.Equal(12, (int)afterDuration["persistSendCodeBodiesHours"]);
                Assert.InRange(durationUntil - DateTimeOffset.UtcNow, TimeSpan.FromHours(11.9), TimeSpan.FromHours(12.1));

                var offResult = RvtMcpConfig.TryApplySettings(
                    new RvtMcpConfig.SettingsPatch { PersistSendCodeBodies = false }, path);
                Assert.Contains("persistSendCodeBodies", offResult.Applied);
                var afterOff = ParseRaw(path);
                Assert.Equal(12, (int)afterOff["persistSendCodeBodiesHours"]);
                Assert.False(afterOff.ContainsKey("persistSendCodeBodiesUntil"));

                var onResult = RvtMcpConfig.TryApplySettings(
                    new RvtMcpConfig.SettingsPatch { PersistSendCodeBodies = true }, path);
                Assert.Contains("persistSendCodeBodies", onResult.Applied);
                var afterOn = ParseRaw(path);
                Assert.Equal(12, (int)afterOn["persistSendCodeBodiesHours"]);
                Assert.True(DateTimeOffset.Parse((string)afterOn["persistSendCodeBodiesUntil"]) > DateTimeOffset.UtcNow.AddHours(11.9));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Invalid_durations_normalize_to_spec_defaults()
        {
            var config = new RvtMcpConfig { ToastIdleSeconds = 7, PersistSendCodeBodiesHours = 99 };
            Assert.Equal(RvtMcpConfig.DefaultToastIdleSeconds, config.ToastIdleSecondsOrDefault);
            Assert.Equal(RvtMcpConfig.DefaultPersistSendCodeBodiesHours, config.PersistSendCodeBodiesHoursOrDefault);

            var path = NewConfigPath();
            try
            {
                File.WriteAllText(path, "{}");
                var result = RvtMcpConfig.TryApplySettings(new RvtMcpConfig.SettingsPatch
                {
                    ToastIdleSeconds = 7,
                    PersistSendCodeBodiesHours = 99,
                }, path);
                Assert.Equal(2, result.Applied.Count);
                var root = ParseRaw(path);
                Assert.Equal(20, (int)root["toastIdleSeconds"]);
                Assert.Equal(4, (int)root["persistSendCodeBodiesHours"]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Theory]
        [InlineData(10, 10)]
        [InlineData(20, 20)]
        [InlineData(30, 30)]
        [InlineData(60, 60)]
        [InlineData(9, 20)]
        [InlineData(61, 20)]
        public void Toast_idle_duration_accepts_only_spec_values(int input, int expected)
        {
            Assert.Equal(expected, RvtMcpConfig.NormalizeToastIdleSeconds(input));
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(48, 48)]
        [InlineData(0, 4)]
        [InlineData(49, 4)]
        public void Journal_duration_is_clamped_to_one_to_48_hours(int input, int expected)
        {
            Assert.Equal(expected, RvtMcpConfig.NormalizePersistSendCodeBodiesHours(input));
        }

        [Fact]
        public void Direct_duration_save_rearms_an_active_journal()
        {
            var path = NewConfigPath();
            try
            {
                File.WriteAllText(path, "{\"persistSendCodeBodies\":true,\"persistSendCodeBodiesHours\":4,\"persistSendCodeBodiesUntil\":\""
                    + DateTimeOffset.UtcNow.AddHours(1).ToString("O") + "\"}");
                string error;
                Assert.True(RvtMcpConfig.TrySavePersistSendCodeBodiesHours(12, out error, path), error);
                var root = ParseRaw(path);
                var until = DateTimeOffset.Parse((string)root["persistSendCodeBodiesUntil"]);
                Assert.Equal(12, (int)root["persistSendCodeBodiesHours"]);
                Assert.InRange(until - DateTimeOffset.UtcNow, TimeSpan.FromHours(11.9), TimeSpan.FromHours(12.1));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void TrySave_rejects_a_non_object_config_without_replacing_it()
        {
            var path = NewConfigPath();
            try
            {
                const string original = "[]";
                File.WriteAllText(path, original);
                string error;
                Assert.False(RvtMcpConfig.TrySaveEnableToast(true, out error, path));
                Assert.Equal(original, File.ReadAllText(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void TryClear_rejects_paths_outside_profile_or_test_sandbox()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "rvtmcp-task4-outside-profile.json");
            string error;
            Assert.False(RvtMcpConfig.TryClearPersistSendCodeBodies(out error, path));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.False(File.Exists(path));
        }
    }
}
