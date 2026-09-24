using System.Reflection;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    public class EmbeddedCatalogTests
    {
        private static readonly Assembly Asm = typeof(EmbeddedCatalogTests).Assembly;

        [Fact]
        public void Load_TestFixture_ReadsStrings_SkipsMetaAndNonStrings()
        {
            var table = EmbeddedCatalog.Load(Asm, "tt");
            Assert.NotNull(table);
            Assert.Equal("fixture-value", table["tt.known"]);
            Assert.Equal("N = {count:n}", table["tt.count"]);
            Assert.False(table.ContainsKey("_meta"));
            Assert.False(table.ContainsKey("tt.nonstring.bad"));
        }

        [Fact]
        public void Load_ShippedEnglish_IsEmbedded()
        {
            var en = EmbeddedCatalog.Load(Asm, "en");
            Assert.NotNull(en);
            Assert.True(en.Count > 0);
            Assert.Equal("Language", en["ribbon.language.label"]);
        }

        [Fact]
        public void Load_MissingLocale_ReturnsNull()
        {
            Assert.Null(EmbeddedCatalog.Load(Asm, "xx"));
            Assert.Null(EmbeddedCatalog.Load(Asm, "vi"));
        }

        [Fact]
        public void ResourceName_MatchesLogicalNameConvention()
        {
            Assert.Equal("RvtMcp.Localization.strings.de.json", EmbeddedCatalog.ResourceName("de"));
        }
    }
}
