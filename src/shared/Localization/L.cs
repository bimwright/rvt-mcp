using System;
using System.Threading;
using System.Threading.Tasks;

namespace RvtMcp.Plugin.Localization
{
    /// <summary>
    /// Static facade over the current <see cref="StringTable"/>. The table reference is
    /// swapped atomically so reads need no lock. <see cref="Changed"/> may fire on the
    /// watcher thread — subscribers marshal to their own dispatcher. A generation
    /// counter drops stale async builds. Spec §5.1.
    /// </summary>
    public static class L
    {
        private static volatile StringTable _current;
        private static int _version;
        private static int _generation;
        private static string _revitLanguageName = "Unknown";
        private static volatile string _desiredLocale = "en";
        private static Func<string, StringTable> _buildFor;
        private static Action<StringTable> _afterSwap;
        private static readonly object _swapGate = new object();

        /// <summary>Test hook: run reload builds inline instead of on a thread-pool thread.</summary>
        internal static bool ForceSyncBuilds;

        public static string Locale => _current?.Locale ?? "en";
        public static int Version => _version;
        public static event EventHandler Changed;

        /// <summary>Never throws. Before init (or key missing everywhere) returns the key.</summary>
        public static string T(string key, params (string Name, object Value)[] args)
        {
            var t = _current;
            if (t == null) return key ?? string.Empty;
            return t.T(key, args);
        }

        /// <summary>
        /// Called once by LocalizationHost on the main thread, before toasts/ribbon exist.
        /// <paramref name="mergedUiLanguage"/> is config.UiLanguage (env already overlaid).
        /// </summary>
        internal static void Initialize(string revitLanguageName, string mergedUiLanguage,
            Func<string, StringTable> buildFor, Action<StringTable> afterSwap)
        {
            Interlocked.Increment(ref _generation);   // drop any in-flight build from a prior session
            _revitLanguageName = revitLanguageName ?? "Unknown";
            _buildFor = buildFor;
            _afterSwap = afterSwap;
            var code = LocaleResolver.NormalizeCode(mergedUiLanguage);
            _desiredLocale = LocaleResolver.ResolveLocale(_revitLanguageName, code);
            Swap(buildFor(_desiredLocale));
        }

        /// <summary>Manual switch. <paramref name="code"/> is "auto" or a locale code;
        /// raw values are normalized. Builds async (generation-stamped).</summary>
        public static void SetLanguage(string code)
        {
            var locale = LocaleResolver.ResolveLocale(
                _revitLanguageName, LocaleResolver.NormalizeCode(code));
            _desiredLocale = locale;
            RequestBuild(locale);
        }

        /// <summary>Called by the override-folder watcher. Rebuilds the requested
        /// locale — not the visible one — so a reload mid-switch cannot cancel the
        /// pending language change (it only needs to beat the older generation).</summary>
        internal static void RequestReload()
        {
            RequestBuild(_desiredLocale ?? (_current != null ? _current.Locale : "en"));
        }

        /// <summary>Diagnostic: why the last build didn't swap.</summary>
        internal static string LastSkipReason;

        private static void RequestBuild(string locale)
        {
            var buildFor = _buildFor;
            if (buildFor == null) { LastSkipReason = "no_buildFor"; return; }
            var gen = Interlocked.Increment(ref _generation);
            if (ForceSyncBuilds)
            {
                BuildAndSwap(buildFor, locale, gen);
                return;
            }
            Task.Run(() => BuildAndSwap(buildFor, locale, gen));
        }

        private static void BuildAndSwap(Func<string, StringTable> buildFor, string locale, int gen)
        {
            StringTable table;
            try { table = buildFor(locale); }
            catch (Exception ex) { LastSkipReason = "throw:" + ex.GetType().Name + ":" + ex.Message; return; }
            if (table == null) { LastSkipReason = "null_table"; return; }
            lock (_swapGate)   // staleness check + swap must be atomic — a newer request may land between them
            {
                if (gen != _generation) { LastSkipReason = "stale"; return; }
                LastSkipReason = null;
                Swap(table);
            }
        }

        private static void Swap(StringTable table)
        {
            _current = table;
            Interlocked.Increment(ref _version);
            try { _afterSwap?.Invoke(table); } catch { }
            var handler = Changed;
            if (handler != null)
            {
                // one throwing subscriber must not starve the rest of the invocation list
                foreach (EventHandler h in handler.GetInvocationList())
                {
                    try { h(null, EventArgs.Empty); } catch { }
                }
            }
        }

        // ---- test hooks (linked sources compile into the test assembly) ----

        internal static void InitializeForTests(StringTable table)
        {
            _buildFor = null;
            _afterSwap = null;
            _revitLanguageName = "Unknown";
            _desiredLocale = table?.Locale ?? "en";
            Interlocked.Increment(ref _generation);   // never rewind — stale builds from earlier tests must stay stale
            Swap(table);
        }

        internal static void ResetForTests()
        {
            _current = null;
            _version = 0;
            Interlocked.Increment(ref _generation);
            _buildFor = null;
            _afterSwap = null;
            _revitLanguageName = "Unknown";
            _desiredLocale = "en";
            ForceSyncBuilds = false;
            Changed = null;
        }
    }
}
