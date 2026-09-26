using System;
using System.IO;
using RvtMcp.Plugin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class RvtMcpConfigPersistSendCodeTests : IDisposable
    {
        private readonly string _tempConfigPath;

        public RvtMcpConfigPersistSendCodeTests()
        {
            _tempConfigPath = Path.Combine(Path.GetTempPath(), $"rvtmcp-test-config-{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            if (File.Exists(_tempConfigPath))
            {
                try { File.Delete(_tempConfigPath); } catch { }
            }
        }

        [Fact]
        public void Default_IsPersistSendCodeBodiesActive_IsFalse()
        {
            var config = new RvtMcpConfig();
            Assert.False(config.IsPersistSendCodeBodiesActive());
        }

        [Fact]
        public void Active_WhenPersistTrue_AndFutureUntil()
        {
            var config = new RvtMcpConfig
            {
                PersistSendCodeBodies = true,
                PersistSendCodeBodiesUntil = DateTimeOffset.UtcNow.AddHours(2).ToString("o")
            };
            Assert.True(config.IsPersistSendCodeBodiesActive());
        }

        [Fact]
        public void Inactive_WhenPersistTrue_AndPastUntil()
        {
            var config = new RvtMcpConfig
            {
                PersistSendCodeBodies = true,
                PersistSendCodeBodiesUntil = DateTimeOffset.UtcNow.AddHours(-1).ToString("o")
            };
            Assert.False(config.IsPersistSendCodeBodiesActive());
        }

        [Fact]
        public void Inactive_WhenUntilInvalid()
        {
            var config = new RvtMcpConfig
            {
                PersistSendCodeBodies = true,
                PersistSendCodeBodiesUntil = "invalid-date"
            };
            Assert.False(config.IsPersistSendCodeBodiesActive());
        }

        [Fact]
        public void SavePersist_PreservesOtherKeys()
        {
            // First save enableToast
            RvtMcpConfig.SaveEnableToast(true, _tempConfigPath);

            var until = DateTimeOffset.UtcNow.AddHours(4);
            RvtMcpConfig.SavePersistSendCodeBodies(true, until, _tempConfigPath);

            var json = File.ReadAllText(_tempConfigPath);
            var root = JObject.Parse(json);

            Assert.True(root.Value<bool>("enableToast"));
            Assert.True(root.Value<bool>("persistSendCodeBodies"));
            Assert.NotNull(root.Value<string>("persistSendCodeBodiesUntil"));

            // Read load
            var config = RvtMcpConfig.Load(args: null, configFilePath: _tempConfigPath);
            Assert.True(config.EnableToastOrDefault);
            Assert.True(config.IsPersistSendCodeBodiesActive());
        }

        [Fact]
        public void Load_ExpiredUntil_ClearsDiskConfig()
        {
            // Save expired
            var until = DateTimeOffset.UtcNow.AddHours(-2);
            RvtMcpConfig.SavePersistSendCodeBodies(true, until, _tempConfigPath);

            // Load should auto-clear and set in-memory to false/null
            var config = RvtMcpConfig.Load(args: null, configFilePath: _tempConfigPath);

            Assert.False(config.IsPersistSendCodeBodiesActive());
            
            // Check disk has been cleared
            var json = File.ReadAllText(_tempConfigPath);
            var root = JObject.Parse(json);
            Assert.Null(root["persistSendCodeBodies"]);
            Assert.Null(root["persistSendCodeBodiesUntil"]);
            Assert.True(root.Value<bool>("persistSendCodeBodiesRequiresExplicitEnable"));
        }

        [Fact]
        public void CLI_Args_ApplyCorrectly()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--persist-send-code-bodies" }, _tempConfigPath);
            Assert.True(config.PersistSendCodeBodies);
            Assert.NotNull(config.PersistSendCodeBodiesUntil); // Default 4h set when helper evaluates or ApplyCliArgs sets it.
            
            // Let's verify custom TTL
            config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--persist-send-code-bodies-for", "90m" }, _tempConfigPath);
            Assert.True(config.PersistSendCodeBodies);
            
            DateTimeOffset parsedUntil = DateTimeOffset.Parse(config.PersistSendCodeBodiesUntil);
            var diff = parsedUntil - DateTimeOffset.UtcNow;
            Assert.True(diff.TotalMinutes > 85 && diff.TotalMinutes < 95);

            // Let's verify clamp max
            config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--persist-send-code-bodies-for", "5d" }, _tempConfigPath);
            parsedUntil = DateTimeOffset.Parse(config.PersistSendCodeBodiesUntil);
            diff = parsedUntil - DateTimeOffset.UtcNow;
            Assert.True(diff.TotalDays > 1.9 && diff.TotalDays < 2.1); // Clamped to 2d

            // Disable
            config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--no-persist-send-code-bodies" }, _tempConfigPath);
            Assert.False(config.PersistSendCodeBodies);
            Assert.Null(config.PersistSendCodeBodiesUntil);
        }

        [Fact]
        public void Env_Vars_ApplyCorrectly()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyEnvVars(config, k =>
            {
                if (k == RvtMcpConfig.EnvPersistSendCodeBodies) return "1";
                if (k == RvtMcpConfig.EnvPersistSendCodeBodiesTtl) return "12h";
                return null;
            }, _tempConfigPath);

            Assert.True(config.PersistSendCodeBodies);
            Assert.NotNull(config.PersistSendCodeBodiesUntil);
            var parsedUntil = DateTimeOffset.Parse(config.PersistSendCodeBodiesUntil);
            var diff = parsedUntil - DateTimeOffset.UtcNow;
            Assert.True(diff.TotalHours > 11 && diff.TotalHours < 13);
        }

        [Fact]
        public void Load_Expired_StickyEnvDoesNotRevive()
        {
            var until = DateTimeOffset.UtcNow.AddHours(-2);
            RvtMcpConfig.SavePersistSendCodeBodies(true, until, _tempConfigPath);

            var config = RvtMcpConfig.Load(
                args: null,
                configFilePath: _tempConfigPath,
                envLookup: k => k == RvtMcpConfig.EnvPersistSendCodeBodies ? "1" : null);

            Assert.False(config.IsPersistSendCodeBodiesActive());
            Assert.True(config.PersistSendCodeBodiesRequiresExplicitEnable);

            var root = JObject.Parse(File.ReadAllText(_tempConfigPath));
            Assert.Null(root["persistSendCodeBodies"]);
            Assert.Null(root["persistSendCodeBodiesUntil"]);
            Assert.True(root.Value<bool>("persistSendCodeBodiesRequiresExplicitEnable"));
        }

        [Fact]
        public void Env_DoesNotResetUntil_WhenAlreadyActive()
        {
            var until = DateTimeOffset.UtcNow.AddHours(3);
            RvtMcpConfig.SavePersistSendCodeBodies(true, until, _tempConfigPath);
            var untilBefore = JObject.Parse(File.ReadAllText(_tempConfigPath))
                .Value<string>("persistSendCodeBodiesUntil");

            var config = RvtMcpConfig.Load(
                args: null,
                configFilePath: _tempConfigPath,
                envLookup: k =>
                {
                    if (k == RvtMcpConfig.EnvPersistSendCodeBodies) return "1";
                    if (k == RvtMcpConfig.EnvPersistSendCodeBodiesTtl) return "12h";
                    return null;
                });

            Assert.True(config.IsPersistSendCodeBodiesActive());
            var untilAfter = JObject.Parse(File.ReadAllText(_tempConfigPath))
                .Value<string>("persistSendCodeBodiesUntil");
            Assert.Equal(untilBefore, untilAfter);
        }

        [Fact]
        public void CLI_ReEnable_AfterExpiry_ClearsExplicitGate()
        {
            var until = DateTimeOffset.UtcNow.AddHours(-1);
            RvtMcpConfig.SavePersistSendCodeBodies(true, until, _tempConfigPath);
            var blocked = RvtMcpConfig.Load(
                args: null,
                configFilePath: _tempConfigPath,
                envLookup: k => k == RvtMcpConfig.EnvPersistSendCodeBodies ? "1" : null);
            Assert.False(blocked.IsPersistSendCodeBodiesActive());

            var config = RvtMcpConfig.Load(
                args: new[] { "--persist-send-code-bodies" },
                configFilePath: _tempConfigPath,
                envLookup: k => k == RvtMcpConfig.EnvPersistSendCodeBodies ? "1" : null);

            Assert.True(config.IsPersistSendCodeBodiesActive());
            Assert.True(config.PersistSendCodeBodiesRequiresExplicitEnable != true);
            var root = JObject.Parse(File.ReadAllText(_tempConfigPath));
            Assert.Null(root["persistSendCodeBodiesRequiresExplicitEnable"]);
        }
    }
}
