using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace RvtMcp.Tests
{
    public class PluginHistoryWindowWiringTests
    {
        [Theory]
        [InlineData("plugin-r22")]
        [InlineData("plugin-r23")]
        [InlineData("plugin-r24")]
        [InlineData("plugin-r25")]
        [InlineData("plugin-r26")]
        [InlineData("plugin-r27")]
        public void ShowOrFocusHistoryWindow_opens_shared_HistoryWindow(string pluginFolder)
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", pluginFolder, "App.cs"));

            Assert.Contains("new HistoryWindow(", source);
            Assert.Contains("_historyWindow?.Close()", source);
            Assert.DoesNotContain("History window is not yet implemented", source);
        }

        [Theory]
        [InlineData("plugin-r22")]
        [InlineData("plugin-r23")]
        [InlineData("plugin-r24")]
        [InlineData("plugin-r25")]
        [InlineData("plugin-r26")]
        [InlineData("plugin-r27")]
        public void LocalizationHost_init_sits_between_config_load_and_ribbon(string pluginFolder)
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", pluginFolder, "App.cs"));

            var config = source.IndexOf("RvtMcpConfig.Load", System.StringComparison.Ordinal);
            var init = source.IndexOf("LocalizationHost.InitializePlugin", System.StringComparison.Ordinal);
            var ribbon = source.IndexOf("RibbonSetup.Create", System.StringComparison.Ordinal);

            Assert.True(config >= 0 && init > config && ribbon > init,
                $"{pluginFolder}: expected Load → InitializePlugin → RibbonSetup ordering " +
                $"(load={config}, init={init}, ribbon={ribbon})");
            Assert.Contains("LocalizationHost.ShutdownPlugin()", source);
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
