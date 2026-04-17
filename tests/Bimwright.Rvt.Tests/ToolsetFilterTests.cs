using System.Collections.Generic;
using System.Linq;
using Bimwright.Rvt.Plugin;
using Bimwright.Rvt.Server;
using Xunit;

namespace Bimwright.Rvt.Tests
{
    public class ToolsetFilterTests
    {
        // --- Defaults ------------------------------------------------------

        [Fact]
        public void Resolve_NullConfig_ReturnsDefaults()
        {
            var set = ToolsetFilter.Resolve(null);
            Assert.Equal(
                new[] { "create", "meta", "query", "view" },
                set.OrderBy(s => s).ToArray());
        }

        [Fact]
        public void Resolve_EmptyToolsets_ReturnsDefaults()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig { Toolsets = new List<string>() });
            Assert.Equal(
                new[] { "create", "meta", "query", "view" },
                set.OrderBy(s => s).ToArray());
        }

        [Fact]
        public void Resolve_Defaults_MatchesDefaultOnArray()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig());
            Assert.Equal(ToolsetFilter.DefaultOn.OrderBy(s => s), set.OrderBy(s => s));
        }

        // --- Explicit toolsets --------------------------------------------

        [Fact]
        public void Resolve_ExplicitToolsets_OnlyRequested()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig
            {
                Toolsets = new List<string> { "query" }
            });
            Assert.Single(set);
            Assert.Contains("query", set);
        }

        [Fact]
        public void Resolve_CaseInsensitive()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig
            {
                Toolsets = new List<string> { "QUERY", "Create" }
            });
            Assert.Contains("query", set);
            Assert.Contains("create", set);
        }

        [Fact]
        public void Resolve_UnknownToolsets_DroppedSilently()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig
            {
                Toolsets = new List<string> { "query", "typo-toolset", "view" }
            });
            Assert.Equal(new[] { "query", "view" }, set.OrderBy(s => s).ToArray());
        }

        // --- "all" shortcut -----------------------------------------------

        [Fact]
        public void Resolve_All_ExpandsToAllKnownToolsets()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig
            {
                Toolsets = new List<string> { "all" }
            });
            Assert.Equal(ToolsetFilter.KnownToolsets.Length, set.Count);
            foreach (var t in ToolsetFilter.KnownToolsets) Assert.Contains(t, set);
        }

        [Fact]
        public void Resolve_AllCaseInsensitive()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig
            {
                Toolsets = new List<string> { "ALL" }
            });
            Assert.Equal(ToolsetFilter.KnownToolsets.Length, set.Count);
        }

        // --- --read-only shortcut -----------------------------------------

        [Fact]
        public void Resolve_ReadOnly_StripsCreateModifyDelete()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig
            {
                Toolsets = new List<string> { "all" },
                ReadOnly = true,
            });
            Assert.DoesNotContain("create", set);
            Assert.DoesNotContain("modify", set);
            Assert.DoesNotContain("delete", set);
            // Non-write toolsets survive
            Assert.Contains("query", set);
            Assert.Contains("view", set);
            Assert.Contains("export", set);
        }

        [Fact]
        public void Resolve_ReadOnlyWithDefaults_LeavesOnlyReadSafeDefaults()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig { ReadOnly = true });
            // Default = query+create+view+meta. ReadOnly strips create.
            Assert.Equal(
                new[] { "meta", "query", "view" },
                set.OrderBy(s => s).ToArray());
        }

        [Fact]
        public void Resolve_ReadOnlyWithQueryOnly_Unchanged()
        {
            var set = ToolsetFilter.Resolve(new BimwrightConfig
            {
                Toolsets = new List<string> { "query" },
                ReadOnly = true,
            });
            Assert.Single(set);
            Assert.Contains("query", set);
        }

        // --- Invariants ---------------------------------------------------

        [Fact]
        public void KnownToolsets_Contains10Entries()
        {
            Assert.Equal(10, ToolsetFilter.KnownToolsets.Length);
        }

        [Fact]
        public void DefaultOn_IsSubsetOfKnown()
        {
            foreach (var d in ToolsetFilter.DefaultOn)
                Assert.Contains(d, ToolsetFilter.KnownToolsets);
        }

        [Fact]
        public void WriteCapable_AllInKnown()
        {
            foreach (var w in ToolsetFilter.WriteCapable)
                Assert.Contains(w, ToolsetFilter.KnownToolsets);
        }
    }
}
