using System;
using System.IO;
using System.Threading;

namespace RvtMcp.Plugin.Localization
{
    /// <summary>
    /// Watches the per-user override folder for changes to the two files that can
    /// affect the live table — <c>strings.&lt;activeLocale&gt;.json</c> and
    /// <c>strings.en.json</c> — and raises <see cref="Changed"/> after a debounce
    /// (spec §5.2). <see cref="OnFileEvent"/> is the injectable seam so tests never
    /// depend on FileSystemWatcher timing; a zero/negative debounce fires
    /// synchronously. <see cref="Changed"/> may fire on a timer thread —
    /// subscribers marshal to their own dispatcher.
    /// </summary>
    public sealed class OverrideFolderWatcher : IDisposable
    {
        public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

        private readonly Func<string> _activeLocale;
        private readonly TimeSpan _debounce;
        private readonly FileSystemWatcher _fsw;
        private readonly object _gate = new object();
        private Timer _timer;
        private bool _disposed;

        public event EventHandler Changed;

        /// <param name="dir">Override folder. Missing/null dir → injection-only watcher.</param>
        /// <param name="activeLocale">Returns the currently active locale code.</param>
        /// <param name="debounce">Null → <see cref="DefaultDebounce"/>. ≤ 0 → synchronous raise.</param>
        public OverrideFolderWatcher(string dir, Func<string> activeLocale, TimeSpan? debounce = null)
        {
            _activeLocale = activeLocale ?? (() => "en");
            _debounce = debounce ?? DefaultDebounce;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _fsw = new FileSystemWatcher(dir, "strings.*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _fsw.Created += (s, e) => OnFileEvent(e.FullPath);
                _fsw.Changed += (s, e) => OnFileEvent(e.FullPath);
                _fsw.Renamed += (s, e) => OnFileEvent(e.FullPath);
            }
        }

        /// <summary>Raw file event — from the watcher or injected by a test.</summary>
        public void OnFileEvent(string path)
        {
            if (_disposed || !IsRelevant(path)) return;
            if (_debounce <= TimeSpan.Zero) { RaiseChanged(); return; }
            lock (_gate)
            {
                if (_disposed) return;
                _timer?.Dispose();
                _timer = new Timer(_ => RaiseChanged(), null, _debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private bool IsRelevant(string path)
        {
            if (path == null) return false;
            var name = Path.GetFileName(path);
            return string.Equals(name, "strings.en.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "strings." + _activeLocale() + ".json", StringComparison.OrdinalIgnoreCase);
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); } catch { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
            if (_fsw != null)
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Dispose();
            }
        }
    }
}
