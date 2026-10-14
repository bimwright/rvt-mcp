using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using RvtMcp.Plugin.ToolBaker;
using Xunit;

namespace RvtMcp.Tests
{
    public class BakedToolRuntimeCacheTests
    {
        [Fact]
        public void RegisterOrUpdate_assigns_stable_ribbon_slots_up_to_limit()
        {
            var cache = new BakedToolRuntimeCache();

            for (var i = 1; i <= 13; i++)
            {
                cache.RegisterOrUpdate(new BakedToolRuntimeEntry(
                    "tool_" + i.ToString("00"),
                    "Tool " + i.ToString("00"),
                    "Description",
                    "{}",
                    "ribbon_plus_mcp",
                    command: new object()));
            }

            Assert.Equal(13, cache.Count);
            Assert.Equal(12, cache.RibbonSlotCount);
            Assert.True(cache.HasRibbonOverflow);
            Assert.Equal("tool_01", cache.GetToolNameForSlot(1));
            Assert.Equal("tool_12", cache.GetToolNameForSlot(12));
            Assert.Null(cache.GetToolNameForSlot(13));
        }

        [Fact]
        public void RegisterOrUpdate_does_not_assign_slot_for_mcp_only_output()
        {
            var cache = new BakedToolRuntimeCache();

            cache.RegisterOrUpdate(new BakedToolRuntimeEntry(
                "mcp_only_tool",
                "MCP Only",
                "Description",
                "{}",
                "mcp_only",
                command: new object()));

            Assert.Single(cache.GetAll());
            Assert.Empty(cache.GetRibbonEntries());
        }

        [Fact]
        public void RegisterOrUpdate_preserves_existing_slot_on_update()
        {
            var cache = new BakedToolRuntimeCache();
            cache.RegisterOrUpdate(new BakedToolRuntimeEntry("tool_a", "A", "First", "{}", "ribbon_plus_mcp", new object()));

            cache.RegisterOrUpdate(new BakedToolRuntimeEntry("tool_a", "A2", "Second", "{}", "ribbon_plus_mcp", new object()));

            var entry = Assert.Single(cache.GetRibbonEntries());
            Assert.Equal(1, entry.RibbonSlot);
            Assert.Equal("A2", entry.DisplayName);
            Assert.Equal("tool_a", cache.GetToolNameForSlot(1));
        }

        [Fact]
        public void Ribbon_slot_commands_open_inbox_instead_of_executing_with_dummy_params()
        {
            var repoRoot = GetRepoRoot();
            var sourcePath = Path.Combine(repoRoot, "src", "shared", "Commands", "RunBakedRibbonSlotCommands.cs");
            var source = File.ReadAllText(sourcePath);

            Assert.Contains("ShowOrFocusBakeInboxWindow", source);
            Assert.DoesNotContain("BuildDummyParamsJson", source);
            Assert.DoesNotContain(".Execute(app,", source);
        }

        [Fact]
        public void Apply_bake_runtime_marker_path_checks_suggestion_source()
        {
            var repoRoot = GetRepoRoot();
            var sourcePath = Path.Combine(repoRoot, "src", "shared", "Handlers", "ToolBaker", "ApplyBakeSuggestionHandler.cs");
            var source = File.ReadAllText(sourcePath);

            Assert.Contains("BakedToolRuntimeSource.IsAllowedForSuggestionSource(source)", source);
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
        }
    }
}
