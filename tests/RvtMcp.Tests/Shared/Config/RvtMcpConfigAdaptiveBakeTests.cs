using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class RvtMcpConfigAdaptiveBakeTests
    {
        private static Func<string, string> EnvLookup(Dictionary<string, string> map) =>
            name => map.TryGetValue(name, out var v) ? v : null;

        [Fact]
        public void Defaults_AdaptiveBakeFlagsOffAndToolbakerOn()
        {
            var config = new RvtMcpConfig();
            Assert.Equal("BIMWRIGHT_ENABLE_ADAPTIVE_BAKE", RvtMcpConfig.EnvEnableAdaptiveBake);
            Assert.Equal("BIMWRIGHT_CACHE_SEND_CODE_BODIES", RvtMcpConfig.EnvCacheSendCodeBodies);
            Assert.False(config.EnableAdaptiveBakeOrDefault);
            Assert.False(config.CacheSendCodeBodiesOrDefault);
            Assert.True(config.EnableToolbakerOrDefault);
        }

        [Fact]
        public void Load_EnableAdaptiveBake_EnvOverridesJson()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, @"{""enableAdaptiveBake"":false}");
                var env = EnvLookup(new Dictionary<string, string>
                {
                    [RvtMcpConfig.EnvEnableAdaptiveBake] = "true",
                });

                var config = RvtMcpConfig.Load(Array.Empty<string>(), path, env);

                Assert.True(config.EnableAdaptiveBake);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Load_EnableAdaptiveBake_CliOverridesEnv()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, @"{""enableAdaptiveBake"":false}");
                var env = EnvLookup(new Dictionary<string, string>
                {
                    [RvtMcpConfig.EnvEnableAdaptiveBake] = "true",
                });

                var config = RvtMcpConfig.Load(new[] { "--disable-adaptive-bake" }, path, env);

                Assert.False(config.EnableAdaptiveBake);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Load_CacheSendCodeBodies_EnvOverridesJson()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, @"{""cacheSendCodeBodies"":false}");
                var env = EnvLookup(new Dictionary<string, string>
                {
                    [RvtMcpConfig.EnvCacheSendCodeBodies] = "yes",
                });

                var config = RvtMcpConfig.Load(Array.Empty<string>(), path, env);

                Assert.True(config.CacheSendCodeBodies);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Load_CacheSendCodeBodies_CliOverridesEnv()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, @"{""cacheSendCodeBodies"":false}");
                var env = EnvLookup(new Dictionary<string, string>
                {
                    [RvtMcpConfig.EnvCacheSendCodeBodies] = "true",
                });

                var config = RvtMcpConfig.Load(new[] { "--no-cache-send-code-bodies" }, path, env);

                Assert.False(config.CacheSendCodeBodies);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void PrintHelp_ListsAdaptiveBakeFlagsAndEnvVars()
        {
            var help = CaptureProgramHelp();

            Assert.Contains("--enable-adaptive-bake", help);
            Assert.Contains("--disable-adaptive-bake", help);
            Assert.Contains("--cache-send-code-bodies", help);
            Assert.Contains("--no-cache-send-code-bodies", help);
            Assert.Contains(RvtMcpConfig.EnvEnableAdaptiveBake, help);
            Assert.Contains(RvtMcpConfig.EnvCacheSendCodeBodies, help);
        }

        private static string CaptureProgramHelp()
        {
            var method = typeof(Program).GetMethod("PrintHelp", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var originalOut = Console.Out;
            using (var writer = new StringWriter())
            {
                try
                {
                    Console.SetOut(writer);
                    method.Invoke(null, null);
                    return writer.ToString();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }
    }
}
