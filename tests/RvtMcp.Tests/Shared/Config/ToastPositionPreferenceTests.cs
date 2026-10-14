using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ToastPositionPreferenceTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rvtmcp-toastpos-" + Guid.NewGuid().ToString("N"));
        private string ConfigPath => Path.Combine(_directory, "rvtmcp.config.json");

        [Fact]
        public void Default_is_top_left_without_drag_including_existing_configs()
        {
            var fresh = new RvtMcpConfig().ToastPositionOrDefault;
            Assert.False(fresh.Right);
            Assert.False(fresh.Bottom);
            Assert.False(fresh.DragEnabled);
            Assert.False(fresh.HasOffset);

            Directory.CreateDirectory(_directory);
            File.WriteAllText(ConfigPath, "{\"enableToast\":true}");
            var loaded = RvtMcpConfig.LoadFromJsonFile(ConfigPath).ToastPositionOrDefault;
            Assert.False(loaded.Right || loaded.Bottom || loaded.DragEnabled || loaded.HasOffset);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, false)]
        public void Saved_position_survives_a_fresh_config_load(bool right, bool bottom, bool drag)
        {
            var options = new ToastPositionOptions(right, bottom, drag, 12.5, -30);
            Assert.True(RvtMcpConfig.TrySaveToastPosition(options, out var error, ConfigPath), error);
            var loaded = RvtMcpConfig.LoadFromJsonFile(ConfigPath).ToastPositionOrDefault;
            Assert.Equal(right, loaded.Right);
            Assert.Equal(bottom, loaded.Bottom);
            Assert.Equal(drag, loaded.DragEnabled);
            Assert.Equal(12.5, loaded.OffsetX);
            Assert.Equal(-30, loaded.OffsetY);
        }

        [Fact]
        public void Clearing_the_offset_removes_the_key_and_keeps_every_other_preference()
        {
            Directory.CreateDirectory(_directory);
            var original = JObject.Parse(@"{""enableToast"":false,""toastIdleSeconds"":60,""showBranding"":true,
                ""persistSendCodeBodies"":true,""persistSendCodeBodiesUntil"":""2099-01-01T00:00:00Z"",
                ""custom"":{""keep"":42}}");
            File.WriteAllText(ConfigPath, original.ToString());

            Assert.True(RvtMcpConfig.TrySaveToastPosition(new ToastPositionOptions(true, true, true, 4, 5), out var error, ConfigPath), error);
            Assert.NotNull(JObject.Parse(File.ReadAllText(ConfigPath))["toastDragOffset"]);

            Assert.True(RvtMcpConfig.TrySaveToastPosition(new ToastPositionOptions(true, true, true), out error, ConfigPath), error);
            var saved = JObject.Parse(File.ReadAllText(ConfigPath));
            Assert.Null(saved["toastDragOffset"]);
            Assert.Equal("right", saved.Value<string>("toastHorizontalAlign"));
            Assert.Equal("bottom", saved.Value<string>("toastVerticalAlign"));
            Assert.True(saved.Value<bool>("toastDragEnabled"));
            foreach (var key in new[] { "toastHorizontalAlign", "toastVerticalAlign", "toastDragEnabled" })
                saved.Remove(key);
            Assert.True(JToken.DeepEquals(original, saved));
        }

        [Theory]
        [InlineData("{\"toastHorizontalAlign\":\"RIGHT\",\"toastVerticalAlign\":7,\"toastDragEnabled\":\"yes\"}")]
        [InlineData("{\"toastHorizontalAlign\":null,\"toastVerticalAlign\":[],\"toastDragEnabled\":1,\"toastDragOffset\":5}")]
        [InlineData("{\"toastDragOffset\":{\"x\":\"3\",\"y\":4}}")]
        [InlineData("{\"toastDragOffset\":{\"x\":3}}")]
        [InlineData("{\"toastDragOffset\":\"nope\"}")]
        public void Unknown_or_mistyped_values_fall_back_to_the_defaults(string json)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(ConfigPath, json);
            var loaded = RvtMcpConfig.LoadFromJsonFile(ConfigPath).ToastPositionOrDefault;
            Assert.False(loaded.Right);
            Assert.False(loaded.Bottom);
            Assert.False(loaded.DragEnabled);
            Assert.False(loaded.HasOffset);
        }

        [Fact]
        public void Saving_reports_invalid_config_without_overwriting_it()
        {
            Directory.CreateDirectory(_directory);
            const string original = "{ invalid json";
            File.WriteAllText(ConfigPath, original);

            Assert.False(RvtMcpConfig.TrySaveToastPosition(new ToastPositionOptions(true), out var error, ConfigPath));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(original, File.ReadAllText(ConfigPath));
        }

        [Fact]
        public async System.Threading.Tasks.Task Concurrent_position_and_branding_saves_do_not_lose_each_others_keys()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(ConfigPath, "{}");
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var tasks = new System.Threading.Tasks.Task[2];
            tasks[0] = System.Threading.Tasks.Task.Run(() =>
            {
                for (var i = 0; i < 25; i++)
                    if (!RvtMcpConfig.TrySaveToastPosition(new ToastPositionOptions(i % 2 == 0, false, true, i, i), out var e, ConfigPath)) failures.Add(e);
            });
            tasks[1] = System.Threading.Tasks.Task.Run(() =>
            {
                for (var i = 0; i < 25; i++)
                    if (!RvtMcpConfig.TrySaveShowBranding(i % 2 == 0, out var e, ConfigPath)) failures.Add(e);
            });
            await System.Threading.Tasks.Task.WhenAll(tasks);

            Assert.Empty(failures);
            var saved = JObject.Parse(File.ReadAllText(ConfigPath));
            Assert.NotNull(saved["showBranding"]);
            Assert.NotNull(saved["toastHorizontalAlign"]);
            Assert.NotNull(saved["toastDragOffset"]);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
            catch { }
        }
    }
}
