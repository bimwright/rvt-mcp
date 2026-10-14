using System.Collections.Generic;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    public class StringTableTests
    {
        private static Dictionary<string, string> En() => new Dictionary<string, string>
        {
            ["greet"] = "Hello",
            ["rooms"] = "Rooms found: {count:n}",
            ["idLine"] = "View id {viewId}",
            ["only.en"] = "English only",
            ["security.warn"] = "Run C#? {file}",
        };

        private static Dictionary<string, string> De() => new Dictionary<string, string>
        {
            ["greet"] = "Hallo",
            ["rooms"] = "Räume gefunden: {count:n}",
            ["idLine"] = "Ansichts-ID {viewId}",
            // "only.en" intentionally absent — falls back to en
            ["security.warn"] = "C# ausführen? {file}",
        };

        private static StringTable Table(string locale = "de",
            IReadOnlyDictionary<string, string> overrides = null,
            System.Action<string> log = null)
            => StringTable.Build(locale, En(), locale == "en" ? null : De(), overrides, log);

        [Fact]
        public void Lookup_UsesLocaleTable()
        {
            Assert.Equal("Hallo", Table().T("greet", null));
        }

        [Fact]
        public void Lookup_FallsBackToEn()
        {
            Assert.Equal("English only", Table().T("only.en", null));
        }

        [Fact]
        public void Lookup_MissingEverywhere_ReturnsKey()
        {
            Assert.Equal("no.such.key", Table().T("no.such.key", null));
        }

        [Fact]
        public void Lookup_MissingKey_LogsOnce()
        {
            var logs = new List<string>();
            var t = Table(log: logs.Add);
            t.T("nope", null);
            t.T("nope", null);
            Assert.Single(logs);
        }

        [Fact]
        public void Override_BeatsEmbedded()
        {
            var t = Table(overrides: new Dictionary<string, string> { ["greet"] = "Tach" });
            Assert.Equal("Tach", t.T("greet", null));
        }

        [Fact]
        public void EnOverride_BeatsEn()
        {
            var t = Table("en", overrides: new Dictionary<string, string> { ["greet"] = "Hey" });
            Assert.Equal("Hey", t.T("greet", null));
        }

        [Fact]
        public void CultureFormat_OptIn_Count()
        {
            Assert.Equal("Räume gefunden: 1.234",
                Table().T("rooms", ("count", 1234)));
        }

        [Fact]
        public void BareToken_StaysInvariant_Id()
        {
            Assert.Equal("Ansichts-ID 1234567",
                Table().T("idLine", ("viewId", 1234567)));
        }

        [Fact]
        public void MissingArg_LeavesLiteral_NoThrow()
        {
            Assert.Equal("Räume gefunden: {count:n}", Table().T("rooms", null));
        }

        [Fact]
        public void NullArg_RendersEmpty()
        {
            var en = new Dictionary<string, string> { ["k"] = "v={v}" };
            var t = StringTable.Build("en", en, null, null);
            Assert.Equal("v=", t.T("k", ("v", null)));
        }

        [Fact]
        public void NullKey_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, Table().T(null, null));
        }

        [Fact]
        public void ParseCatalog_SkipsMetaAndNonStrings()
        {
            var json = "{ \"_meta\": { \"locale\": \"de\" }, \"a\": \"A\", \"n\": 5, \"o\": {\"x\":1} }";
            var cat = StringTable.ParseCatalog(json);
            Assert.Single(cat);
            Assert.Equal("A", cat["a"]);
        }

        [Fact]
        public void LockedKeys_AreSecurityPrefixKeys()
        {
            Assert.Equal(new[] { "security.warn" }, Table().LockedKeys);
        }

        [Fact]
        public void EffectiveEntries_MergeOrder_EnEmbeddedOverride()
        {
            var t = Table(overrides: new Dictionary<string, string> { ["greet"] = "Yo" });
            Assert.Equal("Yo", t.EffectiveEntries["greet"]);
            Assert.Equal("Räume gefunden: {count:n}", t.EffectiveEntries["rooms"]);
            Assert.Equal("English only", t.EffectiveEntries["only.en"]);
        }
    }
}
