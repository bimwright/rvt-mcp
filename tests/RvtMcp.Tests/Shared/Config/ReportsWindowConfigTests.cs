using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ReportsWindowConfigTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rvtmcp-reportswin-" + Guid.NewGuid().ToString("N"));
        private string ConfigPath => Path.Combine(_directory, "rvtmcp.config.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [Fact]
        public void Missing_key_means_wide_without_saved_bounds()
        {
            var fresh = new RvtMcpConfig().ReportsWindowOrDefault;
            Assert.Equal(ReportsWindowMode.Wide, fresh.Mode);
            Assert.Null(fresh.Wide);
            Assert.Null(fresh.Compact);

            var loaded = Load("{\"enableToast\":true}");
            Assert.Equal(ReportsWindowMode.Wide, loaded.Mode);
            Assert.Null(loaded.Wide);
            Assert.Null(loaded.Compact);
        }

        [Fact]
        public void Saved_state_survives_a_fresh_config_load()
        {
            var state = new ReportsWindowState(ReportsWindowMode.Compact,
                new ReportsWindowBounds(120, 80.5, 790, 540), new ReportsWindowBounds(-1500, 140, 390, 610.25));
            Assert.True(RvtMcpConfig.TrySaveReportsWindow(state, out var error, ConfigPath), error);

            var loaded = RvtMcpConfig.LoadFromJsonFile(ConfigPath).ReportsWindowOrDefault;
            Assert.Equal(ReportsWindowMode.Compact, loaded.Mode);
            AssertBounds(loaded.Wide, 120, 80.5, 790, 540);
            AssertBounds(loaded.Compact, -1500, 140, 390, 610.25);
            var saved = (JObject)JObject.Parse(File.ReadAllText(ConfigPath))["reportsWindow"];
            Assert.Equal("compact", saved.Value<string>("mode"));

            Assert.True(RvtMcpConfig.TrySaveReportsWindow(new ReportsWindowState(ReportsWindowMode.Wide, null,
                new ReportsWindowBounds(1, 2, 3, 4)), out error, ConfigPath), error);
            loaded = RvtMcpConfig.LoadFromJsonFile(ConfigPath).ReportsWindowOrDefault;
            Assert.Equal(ReportsWindowMode.Wide, loaded.Mode);
            Assert.Null(loaded.Wide);
            AssertBounds(loaded.Compact, 1, 2, 3, 4);
        }

        [Theory]
        [InlineData("\"COMPACT\"")]
        [InlineData("\"Compact\"")]
        [InlineData("\"tall\"")]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("null")]
        [InlineData("[\"compact\"]")]
        public void Unknown_mode_means_wide(string mode)
        {
            var loaded = Load("{\"reportsWindow\":{\"mode\":" + mode + ",\"compact\":{\"left\":1,\"top\":2,\"width\":390,\"height\":610}}}");
            Assert.Equal(ReportsWindowMode.Wide, loaded.Mode);
            AssertBounds(loaded.Compact, 1, 2, 390, 610);
        }

        [Theory]
        [InlineData("{\"left\":NaN,\"top\":2,\"width\":3,\"height\":4}")]
        [InlineData("{\"left\":1,\"top\":Infinity,\"width\":3,\"height\":4}")]
        [InlineData("{\"left\":\"1\",\"top\":2,\"width\":3,\"height\":4}")]
        [InlineData("{\"left\":1,\"top\":2,\"width\":-3,\"height\":4}")]
        [InlineData("{\"left\":1,\"top\":2,\"width\":3,\"height\":0}")]
        [InlineData("{\"left\":1,\"top\":2,\"width\":20000.5,\"height\":4}")]
        [InlineData("{\"left\":1,\"top\":2,\"width\":3,\"height\":20001}")]
        [InlineData("{\"left\":100000.5,\"top\":2,\"width\":3,\"height\":4}")]
        [InlineData("{\"left\":1,\"top\":-100001,\"width\":3,\"height\":4}")]
        [InlineData("{\"left\":1,\"top\":2,\"width\":3}")]
        [InlineData("{\"left\":1,\"top\":2,\"width\":3,\"height\":null}")]
        [InlineData("[1,2,3,4]")]
        [InlineData("\"wide\"")]
        public void Each_broken_bounds_is_dropped_and_the_other_kept(string broken)
        {
            var good = "{\"left\":-100000,\"top\":100000,\"width\":20000,\"height\":0.5}";
            var wideBroken = Load("{\"reportsWindow\":{\"mode\":\"compact\",\"wide\":" + broken + ",\"compact\":" + good + "}}");
            Assert.Equal(ReportsWindowMode.Compact, wideBroken.Mode);
            Assert.Null(wideBroken.Wide);
            AssertBounds(wideBroken.Compact, -100000, 100000, 20000, 0.5);

            var compactBroken = Load("{\"reportsWindow\":{\"mode\":\"wide\",\"wide\":" + good + ",\"compact\":" + broken + "}}");
            AssertBounds(compactBroken.Wide, -100000, 100000, 20000, 0.5);
            Assert.Null(compactBroken.Compact);
        }

        [Theory]
        [InlineData("{\"reportsWindow\":\"compact\"}")]
        [InlineData("{\"reportsWindow\":[]}")]
        [InlineData("{\"reportsWindow\":null}")]
        public void Mistyped_key_means_the_defaults(string json)
        {
            var loaded = Load(json);
            Assert.Equal(ReportsWindowMode.Wide, loaded.Mode);
            Assert.Null(loaded.Wide);
            Assert.Null(loaded.Compact);
        }

        [Fact]
        public void State_drops_invalid_bounds_it_is_given()
        {
            var state = new ReportsWindowState(ReportsWindowMode.Compact,
                new ReportsWindowBounds(double.NaN, 0, 790, 540), new ReportsWindowBounds(0, 0, 0, 610));
            Assert.Null(state.Wide);
            Assert.Null(state.Compact);
            Assert.Equal(ReportsWindowMode.Wide, new ReportsWindowState((ReportsWindowMode)7).Mode);
        }

        [Fact]
        public void Saving_keeps_every_other_key_including_unknown_ones()
        {
            Directory.CreateDirectory(_directory);
            var original = JObject.Parse(@"{""enableToast"":false,""toastIdleSeconds"":60,""toastDragOffset"":{""x"":4,""y"":5},
                ""reportRetentionDays"":7,""persistSendCodeBodiesUntil"":""2099-01-01T00:00:00Z"",
                ""reportsWindow"":{""mode"":""compact"",""extra"":1},""custom"":{""keep"":42}}");
            File.WriteAllText(ConfigPath, original.ToString());

            var state = new ReportsWindowState(ReportsWindowMode.Wide, new ReportsWindowBounds(10, 20, 790, 540));
            Assert.True(RvtMcpConfig.TrySaveReportsWindow(state, out var error, ConfigPath), error);

            var saved = JObject.Parse(File.ReadAllText(ConfigPath));
            Assert.True(JToken.DeepEquals(JObject.Parse(@"{""mode"":""wide"",""wide"":{""left"":10.0,""top"":20.0,""width"":790.0,""height"":540.0},""textScale"":100}"),
                saved["reportsWindow"]), saved["reportsWindow"].ToString());
            saved.Remove("reportsWindow");
            original.Remove("reportsWindow");
            Assert.True(JToken.DeepEquals(original, saved));
        }

        [Fact]
        public void Text_scale_steps_clamp_at_both_ends_and_survive_every_state_change()
        {
            Assert.Equal(new[] { 90, 100, 110, 125, 150 }, ReportsTextScale.Steps);
            Assert.Equal(110, ReportsTextScale.Larger(100));
            Assert.Equal(150, ReportsTextScale.Larger(150));
            Assert.Equal(90, ReportsTextScale.Smaller(100));
            Assert.Equal(90, ReportsTextScale.Smaller(90));
            Assert.Equal(110, ReportsTextScale.Larger(95)); // an off-step value counts as 100
            Assert.Equal(100, new ReportsWindowState(textScale: 95).TextScale);

            var state = new ReportsWindowState(ReportsWindowMode.Wide, textScale: 125)
                .WithMode(ReportsWindowMode.Compact).WithBounds(ReportsWindowMode.Compact, new ReportsWindowBounds(1, 2, 390, 610));
            Assert.Equal(125, state.TextScale);
            Assert.Equal(150, state.WithTextScale(150).TextScale);
        }

        [Fact]
        public void Saved_text_scale_survives_a_fresh_config_load()
        {
            Assert.True(RvtMcpConfig.TrySaveReportsWindow(new ReportsWindowState(ReportsWindowMode.Compact, textScale: 125), out var error, ConfigPath), error);
            Assert.Equal(125, RvtMcpConfig.LoadFromJsonFile(ConfigPath).ReportsWindowOrDefault.TextScale);
            Assert.Equal(90, Load("{\"reportsWindow\":{\"textScale\":90}}").TextScale);
        }

        [Theory]
        [InlineData("95")]
        [InlineData("-110")]
        [InlineData("110.0")]
        [InlineData("\"110\"")]
        [InlineData("true")]
        [InlineData("null")]
        [InlineData("[110]")]
        [InlineData("99999999999999999999")]
        public void Text_scale_that_is_not_an_integer_step_means_100(string scale)
        {
            Assert.Equal(100, Load("{\"reportsWindow\":{\"mode\":\"compact\",\"textScale\":" + scale + "}}").TextScale);
        }

        private ReportsWindowState Load(string json)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(ConfigPath, json);
            return RvtMcpConfig.LoadFromJsonFile(ConfigPath).ReportsWindowOrDefault;
        }

        private static void AssertBounds(ReportsWindowBounds bounds, double left, double top, double width, double height)
        {
            Assert.NotNull(bounds);
            Assert.Equal(left, bounds.Left);
            Assert.Equal(top, bounds.Top);
            Assert.Equal(width, bounds.Width);
            Assert.Equal(height, bounds.Height);
        }
    }
}
