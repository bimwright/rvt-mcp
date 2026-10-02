using System;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>The spill retention time is a soft setting: 36 h unless a valid value says otherwise.</summary>
    public class SpillRetentionConfigTests
    {
        [Fact]
        public void Nothing_set_means_36_hours()
        {
            Assert.Equal(36, new RvtMcpConfig().SpillRetentionHoursOrDefault);
        }

        [Theory]
        [InlineData("48", 48)]
        [InlineData("1", 1)]
        [InlineData(" 72 ", 72)]
        [InlineData("8760", 8760)]
        [InlineData("0", 36)]
        [InlineData("-3", 36)]
        [InlineData("8761", 36)]
        [InlineData("abc", 36)]
        [InlineData("1.5", 36)]
        [InlineData("", 36)]
        public void The_cli_flag_accepts_a_valid_count_and_falls_back_for_anything_else(string value, int expected)
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--spill-retention-hours", value });
            Assert.Equal(expected, config.SpillRetentionHoursOrDefault);
        }

        [Fact]
        public void The_cli_flag_without_a_value_keeps_the_default_and_does_not_swallow_the_next_flag()
        {
            var config = new RvtMcpConfig();
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--spill-retention-hours" });
            Assert.Equal(36, config.SpillRetentionHoursOrDefault);
        }

        [Fact]
        public void The_cli_overrides_the_environment_and_the_environment_overrides_the_file()
        {
            var config = new RvtMcpConfig { SpillRetentionHours = new JValue(100) };
            RvtMcpConfig.ApplyEnvVars(config,
                name => name == RvtMcpConfig.EnvSpillRetentionHours ? "60" : null, null, false);
            Assert.Equal(60, config.SpillRetentionHoursOrDefault);

            RvtMcpConfig.ApplyCliArgs(config, new[] { "--spill-retention-hours", "48" });
            Assert.Equal(48, config.SpillRetentionHoursOrDefault);
        }

        [Fact]
        public void An_invalid_environment_value_falls_back_to_the_default()
        {
            var config = new RvtMcpConfig { SpillRetentionHours = new JValue(100) };
            RvtMcpConfig.ApplyEnvVars(config,
                name => name == RvtMcpConfig.EnvSpillRetentionHours ? "soon" : null, null, false);
            Assert.Equal(36, config.SpillRetentionHoursOrDefault);
        }

        [Theory]
        [InlineData("{\"spillRetentionHours\": 96}", 96)]
        [InlineData("{\"spillRetentionHours\": \"96\"}", 96)]
        [InlineData("{\"spillRetentionHours\": \"later\"}", 36)]
        [InlineData("{\"spillRetentionHours\": 0}", 36)]
        [InlineData("{\"spillRetentionHours\": 2.5}", 36)]
        [InlineData("{\"spillRetentionHours\": null}", 36)]
        [InlineData("{\"spillRetentionHours\": [1]}", 36)]
        [InlineData("{}", 36)]
        public void The_config_file_value_is_read_leniently(string json, int expected)
        {
            var config = Newtonsoft.Json.JsonConvert.DeserializeObject<RvtMcpConfig>(json);
            Assert.Equal(expected, config.SpillRetentionHoursOrDefault);
        }

        [Fact]
        public void The_setting_reaches_the_plugin_through_the_runtime_options()
        {
            var server = new RvtMcpConfig { SpillRetentionHours = new JValue(72) };
            var plugin = new RvtMcpConfig().WithRuntimeOptions(server.ToRuntimeOptions());
            Assert.Equal(72, plugin.SpillRetentionHoursOrDefault);
        }

        [Fact]
        public void The_default_travels_too_so_both_sides_keep_the_same_time()
        {
            var plugin = new RvtMcpConfig { SpillRetentionHours = new JValue(10) }
                .WithRuntimeOptions(new RvtMcpConfig().ToRuntimeOptions());
            Assert.Equal(36, plugin.SpillRetentionHoursOrDefault);
        }

        [Fact]
        public void The_writer_for_a_config_uses_its_retention()
        {
            Assert.Equal(TimeSpan.FromHours(54),
                ResponseSpillWriter.ForConfig(new RvtMcpConfig { SpillRetentionHours = new JValue(54) }).Retention);
            Assert.Equal(TimeSpan.FromHours(36), ResponseSpillWriter.ForConfig(null).Retention);
        }
    }
}
