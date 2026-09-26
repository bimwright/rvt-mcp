using System.Collections.Generic;
using System.IO;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class RvtMcpConfigTests
    {
        private static System.Func<string, string> EnvLookup(Dictionary<string, string> map) =>
            name => map.TryGetValue(name, out var v) ? v : null;

        // --- ParseBool -----------------------------------------------------

        [Theory]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        [InlineData("yes", true)]
        [InlineData("0", false)]
        [InlineData("false", false)]
        [InlineData("FALSE", false)]
        [InlineData("no", false)]
        public void ParseBool_RecognizedValues_ReturnExpected(string input, bool expected)
        {
            Assert.Equal(expected, RvtMcpConfig.ParseBool(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("garbage")]
        [InlineData("2")]
        public void ParseBool_UnrecognizedValues_ReturnNull(string input)
        {
            Assert.Null(RvtMcpConfig.ParseBool(input));
        }

        // --- ParseCsv ------------------------------------------------------

        [Fact]
        public void ParseCsv_SimpleList_Split()
        {
            var result = RvtMcpConfig.ParseCsv("query,create,view");
            Assert.Equal(new[] { "query", "create", "view" }, result);
        }

        [Fact]
        public void ParseCsv_TrimsWhitespaceAndDropsEmpties()
        {
            var result = RvtMcpConfig.ParseCsv(" query ,, view ,");
            Assert.Equal(new[] { "query", "view" }, result);
        }

        // --- ApplyCliArgs --------------------------------------------------

        [Fact]
        public void ApplyCliArgs_StringArgConsumesNext()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--target", "2025" });
            Assert.Equal("2025", config.Target);
        }

        [Fact]
        public void ApplyCliArgs_ToolsetsParsedAsCsv()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--toolsets", "query,view" });
            Assert.Equal(new[] { "query", "view" }, config.Toolsets);
        }

        [Fact]
        public void ApplyCliArgs_BooleanFlagsSetTrue()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config,
                new[]
                {
                    "--read-only",
                    "--allow-lan-bind",
                    "--enable-toolbaker",
                    "--enable-adaptive-bake",
                    "--cache-send-code-bodies",
                });
            Assert.True(config.ReadOnly);
            Assert.True(config.AllowLanBind);
            Assert.True(config.EnableToolbaker);
            Assert.True(config.EnableAdaptiveBake);
            Assert.True(config.CacheSendCodeBodies);
        }

        [Fact]
        public void ApplyCliArgs_DisableToolbakerSetsFalse()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--disable-toolbaker" });
            Assert.False(config.EnableToolbaker);
        }

        [Fact]
        public void ApplyCliArgs_AdaptiveBakeDisableFlagsSetFalse()
        {
            var config = new RvtMcpConfig
            {
                EnableAdaptiveBake = true,
                CacheSendCodeBodies = true,
            };
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--disable-adaptive-bake", "--no-cache-send-code-bodies" });
            Assert.False(config.EnableAdaptiveBake);
            Assert.False(config.CacheSendCodeBodies);
        }

        [Fact]
        public void ApplyCliArgs_UnknownFlagsIgnored()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--weird-flag", "positional" });
            Assert.Null(config.Target);
            Assert.Null(config.ReadOnly);
        }

        // --- ApplyEnvVars --------------------------------------------------

        [Fact]
        public void ApplyEnvVars_AllFieldsPopulated()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyEnvVars(config, EnvLookup(new Dictionary<string, string>
            {
                [RvtMcpConfig.EnvTarget] = "2027",
                [RvtMcpConfig.EnvToolsets] = "query,create",
                [RvtMcpConfig.EnvReadOnly] = "true",
                [RvtMcpConfig.EnvAllowLanBind] = "1",
                [RvtMcpConfig.EnvEnableToolbaker] = "false",
                [RvtMcpConfig.EnvEnableAdaptiveBake] = "true",
                [RvtMcpConfig.EnvCacheSendCodeBodies] = "1",
                [RvtMcpConfig.EnvEnableToast] = "true",
            }));
            Assert.Equal("2027", config.Target);
            Assert.Equal(new[] { "query", "create" }, config.Toolsets);
            Assert.True(config.ReadOnly);
            Assert.True(config.AllowLanBind);
            Assert.False(config.EnableToolbaker);
            Assert.True(config.EnableAdaptiveBake);
            Assert.True(config.CacheSendCodeBodies);
            Assert.True(config.EnableToast);
        }

        [Fact]
        public void ApplyEnvVars_UnsetVarsLeaveFieldUnchanged()
        {
            var config = new RvtMcpConfig { Target = "2023" };
            RvtMcpConfig.ApplyEnvVars(config, EnvLookup(new Dictionary<string, string>()));
            Assert.Equal("2023", config.Target);
            Assert.Null(config.ReadOnly);
        }

        // --- LoadFromJsonFile ---------------------------------------------

        [Fact]
        public void LoadFromJsonFile_MissingFile_ReturnsNull()
        {
            var result = RvtMcpConfig.LoadFromJsonFile(Path.Combine(Path.GetTempPath(), "definitely-missing.json"));
            Assert.Null(result);
        }

        [Fact]
        public void LoadFromJsonFile_MalformedJson_ReturnsNull()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{not-valid-json");
                Assert.Null(RvtMcpConfig.LoadFromJsonFile(path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadFromJsonFile_ValidJson_PopulatesFields()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, @"{
                    ""target"":""2024"",
                    ""toolsets"":[""query"",""view""],
                    ""readOnly"":true,
                    ""enableToolbaker"":false,
                    ""enableAdaptiveBake"":true,
                    ""cacheSendCodeBodies"":true
                }");
                var config = RvtMcpConfig.LoadFromJsonFile(path);
                Assert.NotNull(config);
                Assert.Equal("2024", config.Target);
                Assert.Equal(new[] { "query", "view" }, config.Toolsets);
                Assert.True(config.ReadOnly);
                Assert.Null(config.AllowLanBind); // absent → null
                Assert.False(config.EnableToolbaker);
                Assert.True(config.EnableAdaptiveBake);
                Assert.True(config.CacheSendCodeBodies);
            }
            finally { File.Delete(path); }
        }

        // --- Load (end-to-end precedence: CLI > env > JSON) ----------------

        [Fact]
        public void Load_CliOverridesEnvOverridesJson()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, @"{""target"":""2022"",""readOnly"":false}");

                var env = EnvLookup(new Dictionary<string, string>
                {
                    [RvtMcpConfig.EnvTarget]    = "2025",
                    [RvtMcpConfig.EnvReadOnly]  = "true",
                });

                var cli = new[] { "--target", "2027" }; // CLI only overrides target

                var config = RvtMcpConfig.Load(cli, path, env);

                Assert.Equal("2027", config.Target);   // CLI wins
                Assert.True(config.ReadOnly);          // env wins (CLI didn't set)
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Load_NoArgs_SkipsCliLayer()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, @"{""target"":""2024""}");
                var config = RvtMcpConfig.Load(args: null, configFilePath: path, envLookup: EnvLookup(new Dictionary<string, string>()));
                Assert.Equal("2024", config.Target);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Load_NoSourcesAtAll_ReturnsDefaults()
        {
            var config = RvtMcpConfig.Load(
                args: System.Array.Empty<string>(),
                configFilePath: Path.Combine(Path.GetTempPath(), "never-exists.json"),
                envLookup: EnvLookup(new Dictionary<string, string>()));
            Assert.Null(config.Target);
            Assert.Null(config.Toolsets);
            Assert.False(config.ReadOnlyOrDefault);
            Assert.False(config.AllowLanBindOrDefault);
            Assert.True(config.EnableToolbakerOrDefault); // default ON per aspect #5
            Assert.False(config.EnableAdaptiveBakeOrDefault);
            Assert.False(config.CacheSendCodeBodiesOrDefault);
            Assert.True(config.EnableToastOrDefault);
        }

        // --- OrDefault accessors ------------------------------------------

        [Fact]
        public void OrDefault_UnsetFieldsFallToCodeDefaults()
        {
            var config = new RvtMcpConfig();
            Assert.False(config.ReadOnlyOrDefault);
            Assert.False(config.AllowLanBindOrDefault);
            Assert.True(config.EnableToolbakerOrDefault);
            Assert.False(config.EnableAdaptiveBakeOrDefault);
            Assert.False(config.CacheSendCodeBodiesOrDefault);
            Assert.True(config.EnableToastOrDefault);
        }

        [Fact]
        public void OrDefault_ExplicitValuesWinOverDefaults()
        {
            var config = new RvtMcpConfig
            {
                ReadOnly = true,
                AllowLanBind = true,
                EnableToolbaker = false,
                EnableAdaptiveBake = true,
                CacheSendCodeBodies = true,
                EnableToast = true,
            };
            Assert.True(config.ReadOnlyOrDefault);
            Assert.True(config.AllowLanBindOrDefault);
            Assert.False(config.EnableToolbakerOrDefault);
            Assert.True(config.EnableAdaptiveBakeOrDefault);
            Assert.True(config.CacheSendCodeBodiesOrDefault);
            Assert.True(config.EnableToastOrDefault);
        }

        [Fact]
        public void SaveEnableToast_PreservesOtherKeysAndSetsFlag()
        {
            var path = Path.Combine(Path.GetTempPath(), "rvtmcp-toast-save-" + Path.GetRandomFileName() + ".json");
            try
            {
                File.WriteAllText(path, @"{""target"":""2026"",""readOnly"":true}");
                RvtMcpConfig.SaveEnableToast(true, path);

                var reloaded = RvtMcpConfig.LoadFromJsonFile(path);
                Assert.NotNull(reloaded);
                Assert.Equal("2026", reloaded.Target);
                Assert.True(reloaded.ReadOnly);
                Assert.True(reloaded.EnableToast);

                RvtMcpConfig.SaveEnableToast(false, path);
                reloaded = RvtMcpConfig.LoadFromJsonFile(path);
                Assert.False(reloaded.EnableToast);
                Assert.Equal("2026", reloaded.Target);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void TrySaveEnableToast_reports_malformed_config_without_overwriting_it()
        {
            var path = Path.Combine(Path.GetTempPath(), "rvtmcp-toast-save-invalid-" + Path.GetRandomFileName() + ".json");
            const string original = "{ invalid json";
            try
            {
                File.WriteAllText(path, original);

                string error;
                var ok = RvtMcpConfig.TrySaveEnableToast(true, out error, path);

                Assert.False(ok);
                Assert.False(string.IsNullOrWhiteSpace(error));
                Assert.Equal(original, File.ReadAllText(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
