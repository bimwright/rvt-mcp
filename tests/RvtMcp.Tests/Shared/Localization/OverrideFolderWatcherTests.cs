using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    public class OverrideFolderWatcherTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "rvt-l10n-watch-" + Guid.NewGuid().ToString("N"));

        public OverrideFolderWatcherTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static string P(string name) => Path.Combine(@"C:\locales", name);

        [Fact]
        public void ActiveLocaleFile_RaisesChanged()
        {
            var fired = 0;
            using (var w = new OverrideFolderWatcher(null, () => "de", TimeSpan.Zero))
            {
                w.Changed += (s, e) => fired++;
                w.OnFileEvent(P("strings.de.json"));
            }
            Assert.Equal(1, fired);
        }

        [Fact]
        public void EnglishFallbackFile_RaisesChanged()
        {
            var fired = 0;
            using (var w = new OverrideFolderWatcher(null, () => "de", TimeSpan.Zero))
            {
                w.Changed += (s, e) => fired++;
                w.OnFileEvent(P("strings.en.json"));
            }
            Assert.Equal(1, fired);
        }

        [Fact]
        public void OtherLocaleFile_IsIgnored()
        {
            var fired = 0;
            using (var w = new OverrideFolderWatcher(null, () => "de", TimeSpan.Zero))
            {
                w.Changed += (s, e) => fired++;
                w.OnFileEvent(P("strings.fr.json"));
                w.OnFileEvent(P("strings.ja.json"));
            }
            Assert.Equal(0, fired);
        }

        [Fact]
        public void SidecarFiles_AreIgnored()
        {
            var fired = 0;
            using (var w = new OverrideFolderWatcher(null, () => "de", TimeSpan.Zero))
            {
                w.Changed += (s, e) => fired++;
                w.OnFileEvent(P("_report.de.json"));
                w.OnFileEvent(P("_active.de.json"));
                w.OnFileEvent(P("notes.txt"));
            }
            Assert.Equal(0, fired);
        }

        [Fact]
        public void RelevanceFollowsActiveLocale()
        {
            var active = "de";
            var fired = 0;
            using (var w = new OverrideFolderWatcher(null, () => active, TimeSpan.Zero))
            {
                w.Changed += (s, e) => fired++;
                w.OnFileEvent(P("strings.fr.json"));   // not active → ignored
                active = "fr";
                w.OnFileEvent(P("strings.fr.json"));   // now active → fires
            }
            Assert.Equal(1, fired);
        }

        // The test owns the debounce timers, so a test thread paused longer than the debounce
        // (seen on CI) cannot split the burst into two changes.
        [Fact]
        public void Debounce_CollapsesBurst_IntoSingleChange()
        {
            var timers = new List<FakeTimer>();
            var fired = 0;
            using (var w = new OverrideFolderWatcher(null, () => "de", TimeSpan.FromMilliseconds(60), false,
                (callback, due) => { var timer = new FakeTimer(callback, due); timers.Add(timer); return timer; }))
            {
                w.Changed += (s, e) => fired++;
                for (var i = 0; i < 5; i++)
                    w.OnFileEvent(P("strings.de.json"));   // editor-style write burst

                Assert.Equal(5, timers.Count);
                Assert.All(timers.Take(4), t => Assert.True(t.Disposed));   // each event restarts the window
                Assert.False(timers[4].Disposed);
                Assert.Equal(TimeSpan.FromMilliseconds(60), timers[4].Due);
                foreach (var timer in timers) timer.Elapse();               // a restarted timer never fires
            }
            Assert.Equal(1, fired);
        }

        [Fact]
        public void Debounce_RaisesChanged_OnARealTimer()
        {
            var fired = 0;
            using (var w = new OverrideFolderWatcher(null, () => "de", TimeSpan.FromMilliseconds(60)))
            {
                w.Changed += (s, e) => Interlocked.Increment(ref fired);
                w.OnFileEvent(P("strings.de.json"));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref fired) >= 1, TimeSpan.FromSeconds(30)));
            }
            Assert.Equal(1, Volatile.Read(ref fired));
        }

        private sealed class FakeTimer : IDisposable
        {
            private readonly TimerCallback _callback;
            public FakeTimer(TimerCallback callback, TimeSpan due) { _callback = callback; Due = due; }
            public TimeSpan Due { get; }
            public bool Disposed { get; private set; }
            public void Elapse() { if (!Disposed) _callback(null); }
            public void Dispose() => Disposed = true;
        }

        [Fact]
        public void Dispose_StopsPendingEvents()
        {
            var fired = 0;
            var w = new OverrideFolderWatcher(null, () => "de", TimeSpan.FromMilliseconds(60));
            w.Changed += (s, e) => fired++;
            w.OnFileEvent(P("strings.de.json"));
            w.Dispose();
            Thread.Sleep(200);
            Assert.Equal(0, fired);
        }
    }
}
