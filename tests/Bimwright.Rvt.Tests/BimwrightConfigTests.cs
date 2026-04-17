using System.Collections.Generic;
using System.IO;
using Bimwright.Rvt.Plugin;
using Xunit;

namespace Bimwright.Rvt.Tests
{
    public class BimwrightConfigTests
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
            Assert.Equal(expected, BimwrightConfig.ParseBool(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("garbage")]
        [InlineData("2")]
        public void ParseBool_UnrecognizedValues_ReturnNull(string input)
        {
            Assert.Null(BimwrightConfig.ParseBool(input));
        }

        // --- ParseCsv ------------------------------------------------------

        [Fact]
        public void ParseCsv_SimpleList_Split()
        {
            var result = BimwrightConfig.ParseCsv("query,create,view");
            Assert.Equal(new[] { "query", "create", "view" }, result);
        }

        [Fact]
        public void ParseCsv_TrimsWhitespaceAndDropsEmpties()
        {
            var result = BimwrightConfig.ParseCsv(" query ,, view ,");
            Assert.Equal(new[] { "query", "view" }, result);
        }

        // --- ApplyCliArgs --------------------------------------------------

        [Fact]
        public void ApplyCliArgs_StringArgConsumesNext()
        {
            var config = new BimwrightConfig();
            BimwrightConfig.ApplyCliArgs(config, new[] { "--target", "R25" });
            Assert.Equal("R25", config.Target);
        }

        [Fact]
        public void ApplyCliArgs_ToolsetsParsedAsCsv()
        {
            var config = new BimwrightConfig();
            BimwrightConfig.ApplyCliArgs(config, new[] { "--toolsets", "query,view" });
            Assert.Equal(new[] { "query", "view" }, config.Toolsets);
        }

        [Fact]
        public void ApplyCliArgs_BooleanFlagsSetTrue()
        {
            var config = new BimwrightConfig();
            BimwrightConfig.ApplyCliArgs(config,
                new[] { "--read-only", "--allow-lan-bind", "--enable-toolbaker" });
            Assert.True(config.ReadOnly);
            Assert.True(config.AllowLanBind);
            Assert.True(config.EnableToolbaker);
        }

        [Fact]
        public void ApplyCliArgs_DisableToolbakerSetsFalse()
        {
            var config = new BimwrightConfig();
            BimwrightConfig.ApplyCliArgs(config, new[] { "--disable-toolbaker" });
            Assert.False(config.EnableToolbaker);
        }

        [Fact]
        public void ApplyCliArgs_UnknownFlagsIgnored()
        {
            var config = new BimwrightConfig();
            BimwrightConfig.ApplyCliArgs(config, new[] { "--weird-flag", "positional" });
            Assert.Null(config.Target);
            Assert.Null(config.ReadOnly);
        }

        // --- ApplyEnvVars --------------------------------------------------

        [Fact]
        public void ApplyEnvVars_AllFieldsPopulated()
        {
            var config = new BimwrightConfig();
            BimwrightConfig.ApplyEnvVars(config, EnvLookup(new Dictionary<string, string>
            {
                [BimwrightConfig.EnvTarget] = "R27",
                [BimwrightConfig.EnvToolsets] = "query,create",
                [BimwrightConfig.EnvReadOnly] = "true",
                [BimwrightConfig.EnvAllowLanBind] = "1",
                [BimwrightConfig.EnvEnableToolbaker] = "false",
            }));
            Assert.Equal("R27", config.Target);
            Assert.Equal(new[] { "query", "create" }, config.Toolsets);
            Assert.True(config.ReadOnly);
            Assert.True(config.AllowLanBind);
            Assert.False(config.EnableToolbaker);
        }

        [Fact]
        public void ApplyEnvVars_UnsetVarsLeaveFieldUnchanged()
        {
            var config = new BimwrightConfig { Target = "R23" };
            BimwrightConfig.ApplyEnvVars(config, EnvLookup(new Dictionary<string, string>()));
            Assert.Equal("R23", config.Target);
            Assert.Null(config.ReadOnly);
        }

        // --- LoadFromJsonFile ---------------------------------------------

        [Fact]
        public void LoadFromJsonFile_MissingFile_ReturnsNull()
        {
            var result = BimwrightConfig.LoadFromJsonFile(Path.Combine(Path.GetTempPath(), "definitely-missing.json"));
            Assert.Null(result);
        }

        [Fact]
        public void LoadFromJsonFile_MalformedJson_ReturnsNull()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{not-valid-json");
                Assert.Null(BimwrightConfig.LoadFromJsonFile(path));
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
                    ""target"":""R24"",
                    ""toolsets"":[""query"",""view""],
                    ""readOnly"":true,
                    ""enableToolbaker"":false
                }");
                var config = BimwrightConfig.LoadFromJsonFile(path);
                Assert.NotNull(config);
                Assert.Equal("R24", config.Target);
                Assert.Equal(new[] { "query", "view" }, config.Toolsets);
                Assert.True(config.ReadOnly);
                Assert.Null(config.AllowLanBind); // absent → null
                Assert.False(config.EnableToolbaker);
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
                File.WriteAllText(path, @"{""target"":""R22"",""readOnly"":false}");

                var env = EnvLookup(new Dictionary<string, string>
                {
                    [BimwrightConfig.EnvTarget]    = "R25",
                    [BimwrightConfig.EnvReadOnly]  = "true",
                });

                var cli = new[] { "--target", "R27" }; // CLI only overrides target

                var config = BimwrightConfig.Load(cli, path, env);

                Assert.Equal("R27", config.Target);   // CLI wins
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
                File.WriteAllText(path, @"{""target"":""R24""}");
                var config = BimwrightConfig.Load(args: null, configFilePath: path, envLookup: EnvLookup(new Dictionary<string, string>()));
                Assert.Equal("R24", config.Target);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Load_NoSourcesAtAll_ReturnsDefaults()
        {
            var config = BimwrightConfig.Load(
                args: System.Array.Empty<string>(),
                configFilePath: Path.Combine(Path.GetTempPath(), "never-exists.json"),
                envLookup: EnvLookup(new Dictionary<string, string>()));
            Assert.Null(config.Target);
            Assert.Null(config.Toolsets);
            Assert.False(config.ReadOnlyOrDefault);
            Assert.False(config.AllowLanBindOrDefault);
            Assert.True(config.EnableToolbakerOrDefault); // default ON per aspect #5
        }

        // --- OrDefault accessors ------------------------------------------

        [Fact]
        public void OrDefault_UnsetFieldsFallToCodeDefaults()
        {
            var config = new BimwrightConfig();
            Assert.False(config.ReadOnlyOrDefault);
            Assert.False(config.AllowLanBindOrDefault);
            Assert.True(config.EnableToolbakerOrDefault);
        }

        [Fact]
        public void OrDefault_ExplicitValuesWinOverDefaults()
        {
            var config = new BimwrightConfig
            {
                ReadOnly = true,
                AllowLanBind = true,
                EnableToolbaker = false,
            };
            Assert.True(config.ReadOnlyOrDefault);
            Assert.True(config.AllowLanBindOrDefault);
            Assert.False(config.EnableToolbakerOrDefault);
        }
    }
}
