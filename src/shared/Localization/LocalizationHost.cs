using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace RvtMcp.Plugin.Localization
{
    /// <summary>
    /// Owns the localization pipeline for one Revit session: builds tables
    /// (embedded catalog + validated overrides → <see cref="StringTable"/>), wires
    /// the override-folder watcher to <see cref="L.RequestReload"/>, and writes
    /// the <c>_report</c>/<c>_active</c> sidecars after every accepted swap.
    /// Created once in App.OnStartup after RvtMcpConfig.Load (spec §5.2).
    /// </summary>
    public sealed class LocalizationHost : IDisposable
    {
        public static LocalizationHost Current { get; private set; }

        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>();

        private readonly string _overrideDir;
        private readonly Func<string, IReadOnlyDictionary<string, string>> _embeddedLoader;
        private readonly TimeSpan? _watcherDebounce;
        private readonly bool _watchFilesystem;
        private readonly ConditionalWeakTable<StringTable, BuildContext> _contexts =
            new ConditionalWeakTable<StringTable, BuildContext>();
        private OverrideFolderWatcher _watcher;
        private bool _initialized;
        private readonly object _stateLock = new object();

        /// <summary>Last valid accepted overrides per locale — a file-level-invalid
        /// override keeps the previous valid entries (spec §7.2).</summary>
        private readonly Dictionary<string, Dictionary<string, string>> _lastGood =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// <summary>%LOCALAPPDATA%\RvtMcp\locales — shared by all Revit instances.</summary>
        public static string DefaultOverrideDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RvtMcp", "locales");

        public string OverrideDir => _overrideDir;

        /// <param name="overrideDir">Null → <see cref="DefaultOverrideDir"/>.</param>
        /// <param name="embeddedLoader">Null → embedded catalogs of this assembly.</param>
        /// <param name="watcherDebounce">Null → watcher default. ≤ 0 → synchronous.</param>
        /// <param name="watchFilesystem">False → watcher runs injection-only (tests).</param>
        public LocalizationHost(string overrideDir = null,
            Func<string, IReadOnlyDictionary<string, string>> embeddedLoader = null,
            TimeSpan? watcherDebounce = null, bool watchFilesystem = true)
        {
            _overrideDir = overrideDir ?? DefaultOverrideDir;
            _embeddedLoader = embeddedLoader ?? DefaultLoader;
            _watcherDebounce = watcherDebounce;
            _watchFilesystem = watchFilesystem;
        }

        private static IReadOnlyDictionary<string, string> DefaultLoader(string locale)
            => EmbeddedCatalog.Load(typeof(LocalizationHost).Assembly, locale);

        /// <summary>Plugin entry point — call once from App.OnStartup after config load.</summary>
        public static LocalizationHost InitializePlugin(string revitLanguageName, string mergedUiLanguage)
        {
            ShutdownPlugin();
            var host = new LocalizationHost();
            host.Initialize(revitLanguageName, mergedUiLanguage);
            Current = host;
            return host;
        }

        /// <summary>Plugin exit point — call once from App.OnShutdown.</summary>
        public static void ShutdownPlugin()
        {
            var host = Current;
            Current = null;
            host?.Dispose();
        }

        public void Initialize(string revitLanguageName, string mergedUiLanguage, bool startWatcher = true)
        {
            if (_initialized) return;
            _initialized = true;
            try { Directory.CreateDirectory(_overrideDir); } catch { }
            L.Initialize(revitLanguageName, mergedUiLanguage, BuildTable, OnSwap);
            if (startWatcher)
            {
                _watcher = new OverrideFolderWatcher(_overrideDir, () => L.Locale,
                    _watcherDebounce, _watchFilesystem);
                _watcher.Changed += (s, e) => L.RequestReload();
            }
        }

        /// <summary>Test seam — forwards into the watcher's relevance filter + debounce.</summary>
        internal void NotifyOverrideFileEvent(string path) => _watcher?.OnFileEvent(path);

        private string OverridePath(string locale)
            => Path.Combine(_overrideDir, "strings." + locale + ".json");

        private StringTable BuildTable(string locale)
        {
            var enEmbedded = Safe(_embeddedLoader("en"));
            var enValidation = OverrideValidator.ValidateFile(OverridePath("en"), enEmbedded);
            var enEffective = Merge(enEmbedded, LastGood("en", enValidation));

            IReadOnlyDictionary<string, string> embedded = null;
            Dictionary<string, string> overrides = null;
            OverrideValidator.Result validation = null;
            if (locale != "en")
            {
                embedded = _embeddedLoader(locale);
                var localeValidation = OverrideValidator.ValidateFile(OverridePath(locale), enEmbedded);
                overrides = LastGood(locale, localeValidation);
                // en-file problems affect every locale — surface them in the active report too.
                validation = MergeValidation(enValidation, localeValidation);
            }
            else
            {
                validation = enValidation;
            }

            var table = StringTable.Build(locale, enEffective, embedded, overrides);
            var ctx = new BuildContext
            {
                EnEmbedded = enEmbedded,
                Embedded = embedded,
                Validation = validation,
            };
            _contexts.Remove(table);
            _contexts.Add(table, ctx);
            return table;
        }

        private void OnSwap(StringTable table)
        {
            BuildContext ctx;
            if (_contexts.TryGetValue(table, out ctx))
            {
                LocalizationReport.WriteAll(
                    _overrideDir, table.Locale, ctx.EnEmbedded, ctx.Embedded, table, ctx.Validation);
            }
        }

        /// <summary>Accepted entries when the file parsed; the last-good set when it
        /// was file-level invalid. First-ever invalid → empty.</summary>
        private Dictionary<string, string> LastGood(string locale, OverrideValidator.Result validation)
        {
            lock (_stateLock)
            {
                if (validation.FileError != null)
                {
                    Dictionary<string, string> cached;
                    if (_lastGood.TryGetValue(locale, out cached)) return cached;
                    return new Dictionary<string, string>();
                }
                _lastGood[locale] = validation.Accepted;
                return validation.Accepted;
            }
        }

        private static OverrideValidator.Result MergeValidation(
            OverrideValidator.Result enFile, OverrideValidator.Result localeFile)
        {
            if (enFile == null) return localeFile;
            if (localeFile == null || ReferenceEquals(enFile, localeFile)) return enFile;
            var merged = new OverrideValidator.Result
            {
                FileError = localeFile.FileError ?? enFile.FileError,
            };
            merged.Rejected.AddRange(enFile.Rejected);
            merged.Rejected.AddRange(localeFile.Rejected);
            return merged;
        }

        private static IReadOnlyDictionary<string, string> Safe(IReadOnlyDictionary<string, string> d)
            => d ?? Empty;

        private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> baseDict,
            IReadOnlyDictionary<string, string> overlay)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            if (baseDict != null)
                foreach (var kv in baseDict) merged[kv.Key] = kv.Value;
            if (overlay != null)
                foreach (var kv in overlay) merged[kv.Key] = kv.Value;
            return merged;
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        private sealed class BuildContext
        {
            public IReadOnlyDictionary<string, string> EnEmbedded;
            public IReadOnlyDictionary<string, string> Embedded;
            public OverrideValidator.Result Validation;
        }
    }
}
