using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("L10n")]
    public class LFacadeTests : IDisposable
    {
        public LFacadeTests() => L.ResetForTests();
        public void Dispose() => L.ResetForTests();

        private static StringTable TableFor(string locale) => StringTable.Build(
            locale,
            new Dictionary<string, string> { ["k"] = "EN-" + "value" },
            locale == "en" ? null : new Dictionary<string, string> { ["k"] = locale.ToUpperInvariant() + "-value" },
            null);

        [Fact]
        public void T_BeforeInit_ReturnsKey()
        {
            Assert.Equal("some.key", L.T("some.key"));
        }

        [Fact]
        public void Initialize_ResolvesAutoFromRevitLanguage()
        {
            L.Initialize("German", "auto", TableFor, null);
            Assert.Equal("de", L.Locale);
            Assert.Equal("DE-value", L.T("k"));
        }

        [Fact]
        public void Initialize_ExplicitCodeWins()
        {
            L.Initialize("German", "ja", TableFor, null);
            Assert.Equal("ja", L.Locale);
        }

        [Fact]
        public void Initialize_InvalidCode_BehavesAsAuto()
        {
            L.Initialize("German", "vi", TableFor, null);
            Assert.Equal("de", L.Locale);
        }

        [Fact]
        public void SetLanguage_SwapsTable_BumpsVersion_RaisesChanged()
        {
            L.ForceSyncBuilds = true;
            L.Initialize("English_USA", "auto", TableFor, null);
            var fired = 0;
            L.Changed += (s, e) => fired++;
            var v0 = L.Version;
            L.SetLanguage("ja");
            Assert.Equal("ja", L.Locale);
            Assert.True(L.Version > v0);
            Assert.Equal(1, fired);
        }

        [Fact]
        public void SetLanguage_Auto_ResolvesViaRevitLanguage()
        {
            L.ForceSyncBuilds = true;
            L.Initialize("Japanese", "ja", TableFor, null);
            Assert.Equal("ja", L.Locale);
            L.SetLanguage("auto");
            Assert.Equal("ja", L.Locale);   // auto → Japanese → ja (same table, still a swap)
        }

        [Fact]
        public void StaleBuild_IsDropped()
        {
            var gate = new ManualResetEventSlim(false);
            var jaReturned = new ManualResetEventSlim(false);
            Func<string, StringTable> buildFor = loc =>
            {
                if (loc == "ja") { gate.Wait(TimeSpan.FromSeconds(10)); jaReturned.Set(); }
                return TableFor(loc);
            };
            L.Initialize("English_USA", "auto", buildFor, null);   // en, sync
            Assert.Equal("en", L.Locale);

            L.SetLanguage("ja");   // gen1 — async, blocks on gate
            L.SetLanguage("de");   // gen2 — async, completes first
            Assert.True(SpinWait.SpinUntil(() => L.Locale == "de", TimeSpan.FromSeconds(10)));

            gate.Set();
            Assert.True(jaReturned.Wait(TimeSpan.FromSeconds(10)));
            Thread.Sleep(200);      // let gen1's build reach its staleness check
            Assert.Equal("de", L.Locale);   // stale ja build must not overwrite
        }

        [Fact]
        public async Task ConcurrentReads_DuringSwap_NoThrow()
        {
            L.ForceSyncBuilds = true;
            L.Initialize("English_USA", "auto", TableFor, null);
            var stop = new ManualResetEventSlim(false);
            var readers = Task.Run(() =>
            {
                while (!stop.IsSet) { var _ = L.T("k"); }
            });
            for (var i = 0; i < 200; i++)
                L.SetLanguage(i % 2 == 0 ? "ja" : "de");
            stop.Set();
            await readers;
        }
    }
}
