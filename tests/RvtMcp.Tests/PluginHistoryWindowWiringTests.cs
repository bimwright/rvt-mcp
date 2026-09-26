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
        public void Toast_history_click_is_marshaled_to_the_Revit_dispatcher(string pluginFolder)
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", pluginFolder, "App.cs"));

            Assert.Contains("_revitDispatcher", source);
            Assert.Contains("ShowOrFocusHistoryWindowFromToast", source);
            Assert.Contains("ToastNotifier?.SetHostDispatcher(_revitDispatcher)", source);
            Assert.Contains("dispatcher.BeginInvoke(new Action(ShowOrFocusHistoryWindow)", source);
        }

        [Fact]
        public void Toast_notifier_does_not_open_History_on_the_toast_dispatcher()
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
                "src", "shared", "Views", "Toast", "McpToastNotifier.cs"));

            Assert.Contains("ShowOrFocusHistoryWindowFromToast", source);
            Assert.DoesNotContain("ShowOrFocusHistoryWindow();", source);
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
            // Missing-key + init diagnostics must reach the real debug log file.
            Assert.Contains("Config.UiLanguage, DebugLog", source);
        }

        [Fact]
        public void LocalizationHost_init_is_nonfatal_and_logs_missing_keys()
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
                "src", "shared", "Localization", "LocalizationHost.cs"));

            // Init failure falls back to an English table instead of killing OnStartup.
            Assert.Contains("catch (Exception ex)", source);
            Assert.Contains("L.Initialize(\"Unknown\", \"en\"", source);
            // StringTable.Build gets the log callback — missing keys are logged.
            Assert.Contains("overrides, _log", source);
        }

        [Fact]
        public void ShowMessage_wire_payload_stays_english_while_display_localizes()
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
                "src", "shared", "Handlers", "ShowMessageHandler.cs"));

            Assert.Contains("TaskDialog.Show(title, displayMessage)", source);
            Assert.Contains("message_char_count = wireMessage.Length", source);
        }

        [Fact]
        public void McpEventHandler_unknown_summary_is_localized_and_preserved()
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(),
                "src", "shared", "Infrastructure", "McpEventHandler.cs"));

            Assert.Contains("history.summary.unknown", source);
            Assert.DoesNotContain("$\"Unknown:", source);
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
