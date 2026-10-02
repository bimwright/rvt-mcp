using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// A9 3-layer config (aspect #3 §A9). Single POCO read by both processes:
    /// Server consumes <see cref="Target"/> / <see cref="Toolsets"/> / <see cref="ReadOnly"/>;
    /// Plugin consumes <see cref="AllowLanBind"/> / <see cref="EnableToolbaker"/>;
    /// ToolBaker adaptive services consume <see cref="EnableAdaptiveBake"/> /
    /// <see cref="CacheSendCodeBodies"/>.
    ///
    /// Precedence (high → low): CLI args > env vars (BIMWRIGHT_*) > JSON file.
    /// Fields stay nullable so "not set" is distinguishable from "explicitly default-valued";
    /// resolved defaults are exposed via the *OrDefault accessors.
    /// </summary>
    public class RvtMcpConfig
    {
        public const string EnvTarget                    = "BIMWRIGHT_TARGET";
        public const string EnvToolsets                  = "BIMWRIGHT_TOOLSETS";
        public const string EnvReadOnly                  = "BIMWRIGHT_READ_ONLY";
        public const string EnvAllowLanBind              = "BIMWRIGHT_ALLOW_LAN_BIND";
        public const string EnvEnableToolbaker           = "BIMWRIGHT_ENABLE_TOOLBAKER";
        public const string EnvEnableAdaptiveBake        = "BIMWRIGHT_ENABLE_ADAPTIVE_BAKE";
        public const string EnvCacheSendCodeBodies       = "BIMWRIGHT_CACHE_SEND_CODE_BODIES";
        public const string EnvEnableToast               = "BIMWRIGHT_ENABLE_TOAST";
        public const string EnvPersistSendCodeBodies     = "BIMWRIGHT_PERSIST_SEND_CODE_BODIES";
        public const string EnvPersistSendCodeBodiesTtl  = "BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL";
        public const string EnvUiLanguage                = "BIMWRIGHT_UI_LANGUAGE";
        public const string EnvEnableSendCode = "BIMWRIGHT_ENABLE_SEND_CODE";
        public const string EnvEnableCallLog = "BIMWRIGHT_ENABLE_CALL_LOG";
        public const string EnvEnableChangeHistory = "BIMWRIGHT_ENABLE_CHANGE_HISTORY";
        public const string EnvEnableResponseGuard = "BIMWRIGHT_ENABLE_RESPONSE_GUARD";
        public const string EnvResponseWarnBytes = "BIMWRIGHT_RESPONSE_WARN_BYTES";
        public const string EnvResponseStrongWarnBytes = "BIMWRIGHT_RESPONSE_STRONG_WARN_BYTES";
        public const string EnvResponseBudgetBytes = "BIMWRIGHT_RESPONSE_BUDGET_BYTES";
        public const string EnvMaxResponseBytes = "BIMWRIGHT_MAX_RESPONSE_BYTES";

        public const bool DefaultReadOnly                  = false;
        public const bool DefaultAllowLanBind              = false;
        public const bool DefaultEnableToolbaker           = true;
        public const bool DefaultEnableAdaptiveBake        = false;
        public const bool DefaultCacheSendCodeBodies       = false;
        public const bool DefaultEnableToast               = true;
        public const bool DefaultShowBranding              = false;
        public const bool DefaultPersistSendCodeBodies     = false;
        public const int DefaultToastIdleSeconds           = 20;
        public const int DefaultPersistSendCodeBodiesHours = 4;

        [JsonProperty("target")]
        public string Target { get; set; }

        [JsonProperty("toolsets")]
        public List<string> Toolsets { get; set; }

        [JsonProperty("readOnly")]
        public bool? ReadOnly { get; set; }

        [JsonProperty("enableSendCode")]
        public bool? EnableSendCode { get; set; }

        [JsonProperty("enableCallLog")]
        public bool? EnableCallLog { get; set; }

        [JsonProperty("enableChangeHistory")]
        public bool? EnableChangeHistory { get; set; }

        [JsonProperty("enableResponseGuard")]
        public bool? EnableResponseGuard { get; set; }

        [JsonProperty("responseWarnBytes")]
        public int? ResponseWarnBytes { get; set; }

        [JsonProperty("responseStrongWarnBytes")]
        public int? ResponseStrongWarnBytes { get; set; }

        [JsonProperty("responseBudgetBytes")]
        public int? ResponseBudgetBytes { get; set; }

        [JsonProperty("maxResponseBytes")]
        public int? MaxResponseBytes { get; set; }

        [JsonProperty("allowLanBind")]
        public bool? AllowLanBind { get; set; }

        [JsonProperty("enableToolbaker")]
        public bool? EnableToolbaker { get; set; }

        [JsonProperty("enableAdaptiveBake")]
        public bool? EnableAdaptiveBake { get; set; }

        [JsonProperty("cacheSendCodeBodies")]
        public bool? CacheSendCodeBodies { get; set; }

        [JsonProperty("enableToast")]
        public bool? EnableToast { get; set; }

        /// <summary>Show the activity-card wordmark and product prefix. Saved across Revit sessions.</summary>
        [JsonProperty("showBranding")]
        public bool? ShowBranding { get; set; }

        /// <summary>Toast corner, horizontal: "left" (default) or "right". Unknown values mean left.</summary>
        [JsonProperty("toastHorizontalAlign")]
        public JToken ToastHorizontalAlign { get; set; }

        /// <summary>Toast corner, vertical: "top" (default) or "bottom". Unknown values mean top.</summary>
        [JsonProperty("toastVerticalAlign")]
        public JToken ToastVerticalAlign { get; set; }

        /// <summary>Allow dragging the card by its title row. Off by default.</summary>
        [JsonProperty("toastDragEnabled")]
        public JToken ToastDragEnabled { get; set; }

        /// <summary>Saved drag offset <c>{ "x", "y" }</c> in DIPs, relative to the chosen corner.</summary>
        [JsonProperty("toastDragOffset")]
        public JToken ToastDragOffset { get; set; }

        /// <summary>Idle duration for the single activity toast. Settings accepts 10, 20, 30, or 60 seconds.</summary>
        [JsonProperty("toastIdleSeconds")]
        public int? ToastIdleSeconds { get; set; }

        [JsonProperty("persistSendCodeBodies")]
        public bool? PersistSendCodeBodies { get; set; }

        [JsonProperty("persistSendCodeBodiesUntil")]
        public string PersistSendCodeBodiesUntil { get; set; }

        /// <summary>Journal retention duration in hours. Settings accepts 1 through 48 hours.</summary>
        [JsonProperty("persistSendCodeBodiesHours")]
        public int? PersistSendCodeBodiesHours { get; set; }

        /// <summary>
        /// Set when TTL auto-expires. Sticky env=1 must not re-enable until CLI
        /// (or an explicit new enable that clears this flag) stamps a fresh Until.
        /// </summary>
        [JsonProperty("persistSendCodeBodiesRequiresExplicitEnable")]
        public bool? PersistSendCodeBodiesRequiresExplicitEnable { get; set; }

        /// <summary>
        /// Plugin UI language override: "auto" or a shipped locale code (en, ja, …).
        /// Read by the plugin only; the server ignores it.
        /// </summary>
        [JsonProperty("uiLanguage")]
        public string UiLanguage { get; set; }

        public bool ReadOnlyOrDefault              => ReadOnly           ?? DefaultReadOnly;
        public bool EnableSendCodeOrDefault => !ReadOnlyOrDefault && (EnableSendCode ?? true);
        public bool EnableCallLogOrDefault => EnableCallLog ?? false;
        public bool EnableChangeHistoryOrDefault => EnableChangeHistory ?? true;
        public bool EnableResponseGuardOrDefault => EnableResponseGuard ?? true;
        public int ResponseWarnBytesOrDefault => PositiveBytes(ResponseWarnBytes, 64 * 1024, "responseWarnBytes");
        public int ResponseStrongWarnBytesOrDefault => PositiveBytes(ResponseStrongWarnBytes, 256 * 1024, "responseStrongWarnBytes");
        public int ResponseBudgetBytesOrDefault => PositiveBytes(ResponseBudgetBytes, 700 * 1024, "responseBudgetBytes");
        public int MaxResponseBytesOrDefault => PositiveBytes(MaxResponseBytes, 1024 * 1024, "maxResponseBytes");

        private static int PositiveBytes(int? value, int fallback, string name)
        {
            if (value.HasValue && value.Value < 1024)
                throw new ArgumentException(name + " must be at least 1024 bytes.");
            return value ?? fallback;
        }

        public void ValidateResponseLimits()
        {
            if (ResponseWarnBytesOrDefault > ResponseStrongWarnBytesOrDefault
                || ResponseStrongWarnBytesOrDefault > ResponseBudgetBytesOrDefault
                || ResponseBudgetBytesOrDefault > MaxResponseBytesOrDefault)
                throw new ArgumentException("Response limits must satisfy warn <= strong warning <= budget <= transport cap.");
        }

        // Only session controls cross the authenticated server/plugin boundary. Host UI
        // and code-body retention settings remain owned by the plugin's local config.
        public RvtMcpConfig ToRuntimeOptions()
        {
            return new RvtMcpConfig
            {
                ReadOnly = ReadOnlyOrDefault,
                EnableSendCode = EnableSendCodeOrDefault,
                EnableCallLog = EnableCallLogOrDefault,
                EnableChangeHistory = EnableChangeHistoryOrDefault,
                EnableResponseGuard = EnableResponseGuardOrDefault,
                ResponseWarnBytes = ResponseWarnBytesOrDefault,
                ResponseStrongWarnBytes = ResponseStrongWarnBytesOrDefault,
                ResponseBudgetBytes = ResponseBudgetBytesOrDefault,
                MaxResponseBytes = MaxResponseBytesOrDefault
            };
        }

        public RvtMcpConfig WithRuntimeOptions(RvtMcpConfig runtime)
        {
            if (runtime == null) return this;
            var result = (RvtMcpConfig)MemberwiseClone();
            // A client may tighten host policy, never relax it.
            result.ReadOnly = ReadOnlyOrDefault || runtime.ReadOnlyOrDefault;
            result.EnableSendCode = EnableSendCodeOrDefault && runtime.EnableSendCodeOrDefault;
            result.EnableCallLog = runtime.EnableCallLog ?? EnableCallLog;
            result.EnableChangeHistory = runtime.EnableChangeHistory ?? EnableChangeHistory;
            result.EnableResponseGuard = runtime.EnableResponseGuard ?? EnableResponseGuard;
            result.ResponseWarnBytes = runtime.ResponseWarnBytes ?? ResponseWarnBytes;
            result.ResponseStrongWarnBytes = runtime.ResponseStrongWarnBytes ?? ResponseStrongWarnBytes;
            result.ResponseBudgetBytes = runtime.ResponseBudgetBytes ?? ResponseBudgetBytes;
            result.MaxResponseBytes = runtime.MaxResponseBytes ?? MaxResponseBytes;
            result.ValidateResponseLimits();
            return result;
        }
        public bool AllowLanBindOrDefault          => AllowLanBind       ?? DefaultAllowLanBind;
        public bool EnableToolbakerOrDefault       => EnableToolbaker    ?? DefaultEnableToolbaker;
        public bool EnableAdaptiveBakeOrDefault    => EnableAdaptiveBake ?? DefaultEnableAdaptiveBake;
        public bool CacheSendCodeBodiesOrDefault  => CacheSendCodeBodies ?? DefaultCacheSendCodeBodies;
        public bool EnableToastOrDefault          => EnableToast       ?? DefaultEnableToast;
        public bool ShowBrandingOrDefault         => ShowBranding      ?? DefaultShowBranding;
        public ToastPositionOptions ToastPositionOrDefault => ReadToastPosition(
            ToastHorizontalAlign, ToastVerticalAlign, ToastDragEnabled, ToastDragOffset);
        public int ToastIdleSecondsOrDefault      => NormalizeToastIdleSeconds(ToastIdleSeconds);
        public int PersistSendCodeBodiesHoursOrDefault => NormalizePersistSendCodeBodiesHours(PersistSendCodeBodiesHours);

        public static int NormalizeToastIdleSeconds(int? seconds)
        {
            switch (seconds)
            {
                case 10:
                case 20:
                case 30:
                case 60:
                    return seconds.Value;
                default:
                    return DefaultToastIdleSeconds;
            }
        }

        public static int NormalizePersistSendCodeBodiesHours(int? hours)
        {
            return hours >= 1 && hours <= 48 ? hours.Value : DefaultPersistSendCodeBodiesHours;
        }

        public bool IsPersistSendCodeBodiesActive(DateTimeOffset? now = null)
        {
            if (PersistSendCodeBodies != true) return false;
            if (!DateTimeOffset.TryParse(PersistSendCodeBodiesUntil, out var until)) return false;
            until = until.ToUniversalTime();
            return (now ?? DateTimeOffset.UtcNow) < until;
        }

        public static string DefaultConfigFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "rvt-mcp",
                "rvtmcp.config.json");

        /// <summary>Test hook: redirects every Load that does not pass an explicit path.</summary>
        internal static string ConfigFilePathOverride { get; set; }

        /// <summary>
        /// Load config from JSON → overlay env vars → overlay CLI args. Pass <c>null</c>
        /// for args to skip the CLI layer (plugin-process callers do this since Revit
        /// does not propagate Server args).
        /// </summary>
        public static RvtMcpConfig Load(string[] args = null, string configFilePath = null)
        {
            return Load(args, configFilePath, envLookup: null);
        }

        /// <summary>
        /// Read the settings snapshot without expiry cleanup, CLI processing, or any
        /// write-back. Environment variables are an in-memory overlay only. Settings
        /// uses this method while a dialog is open so merely opening the dialog cannot
        /// mutate the user's config or journal TTL.
        /// </summary>
        public static RvtMcpConfig LoadReadOnly(string configFilePath = null)
        {
            return LoadReadOnly(configFilePath, envLookup: null);
        }

        internal static RvtMcpConfig LoadReadOnly(string configFilePath, Func<string, string> envLookup)
        {
            var path = configFilePath ?? ConfigFilePathOverride ?? DefaultConfigFilePath;
            var config = LoadFromJsonFile(path) ?? new RvtMcpConfig();
            ApplyEnvVars(config, envLookup, configFilePath: null, allowPersistenceWriteBack: false);
            return config;
        }

        internal static RvtMcpConfig Load(string[] args, string configFilePath, Func<string, string> envLookup)
        {
            var path = configFilePath ?? ConfigFilePathOverride ?? DefaultConfigFilePath;
            var config = LoadFromJsonFile(path)
                         ?? new RvtMcpConfig();

            // Check expiry on Load — clear and suppress sticky env re-enable.
            if (config.PersistSendCodeBodies == true)
            {
                var utcNow = DateTimeOffset.UtcNow;
                if (string.IsNullOrEmpty(config.PersistSendCodeBodiesUntil)
                    || !DateTimeOffset.TryParse(config.PersistSendCodeBodiesUntil, out var until)
                    || utcNow >= until.ToUniversalTime())
                {
                    config.PersistSendCodeBodies = null;
                    config.PersistSendCodeBodiesUntil = null;
                    config.PersistSendCodeBodiesRequiresExplicitEnable = true;
                    string cleanupError;
                    TryClearPersistSendCodeBodies(out cleanupError, path, requireExplicitEnable: true);
                }
            }

            ApplyEnvVars(config, envLookup, path);
            if (args != null) ApplyCliArgs(config, args, path);
            return config;
        }

        internal static RvtMcpConfig LoadFromJsonFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            try
            {
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) return null;
                return JsonConvert.DeserializeObject<RvtMcpConfig>(text);
            }
            catch
            {
                // Malformed config = ignore silently, fall back to env/CLI + code defaults.
                // Don't punish the user for a typo in a file that's optional.
                return null;
            }
        }

        internal static void ApplyEnvVars(RvtMcpConfig config, Func<string, string> lookup = null)
        {
            ApplyEnvVars(config, lookup, null, allowPersistenceWriteBack: true);
        }

        internal static void ApplyEnvVars(RvtMcpConfig config, Func<string, string> lookup, string configFilePath)
        {
            ApplyEnvVars(config, lookup, configFilePath, allowPersistenceWriteBack: true);
        }

        internal static void ApplyEnvVars(
            RvtMcpConfig config,
            Func<string, string> lookup,
            string configFilePath,
            bool allowPersistenceWriteBack)
        {
            lookup = lookup ?? Environment.GetEnvironmentVariable;

            var target = lookup(EnvTarget);
            if (!string.IsNullOrWhiteSpace(target)) config.Target = target.Trim();

            var toolsets = lookup(EnvToolsets);
            if (!string.IsNullOrWhiteSpace(toolsets)) config.Toolsets = ParseCsv(toolsets);

            var readOnly = ParseBool(lookup(EnvReadOnly));
            if (readOnly.HasValue) config.ReadOnly = readOnly;

            config.EnableSendCode = ParseBool(lookup(EnvEnableSendCode)) ?? config.EnableSendCode;
            config.EnableCallLog = ParseBool(lookup(EnvEnableCallLog)) ?? config.EnableCallLog;
            config.EnableChangeHistory = ParseBool(lookup(EnvEnableChangeHistory)) ?? config.EnableChangeHistory;
            config.EnableResponseGuard = ParseBool(lookup(EnvEnableResponseGuard)) ?? config.EnableResponseGuard;
            config.ResponseWarnBytes = ParseBytes(lookup(EnvResponseWarnBytes), EnvResponseWarnBytes) ?? config.ResponseWarnBytes;
            config.ResponseStrongWarnBytes = ParseBytes(lookup(EnvResponseStrongWarnBytes), EnvResponseStrongWarnBytes) ?? config.ResponseStrongWarnBytes;
            config.ResponseBudgetBytes = ParseBytes(lookup(EnvResponseBudgetBytes), EnvResponseBudgetBytes) ?? config.ResponseBudgetBytes;
            config.MaxResponseBytes = ParseBytes(lookup(EnvMaxResponseBytes), EnvMaxResponseBytes) ?? config.MaxResponseBytes;

            var allowLan = ParseBool(lookup(EnvAllowLanBind));
            if (allowLan.HasValue) config.AllowLanBind = allowLan;

            var enableBaker = ParseBool(lookup(EnvEnableToolbaker));
            if (enableBaker.HasValue) config.EnableToolbaker = enableBaker;

            var adaptiveBake = ParseBool(lookup(EnvEnableAdaptiveBake));
            if (adaptiveBake.HasValue) config.EnableAdaptiveBake = adaptiveBake;

            var cacheBodies = ParseBool(lookup(EnvCacheSendCodeBodies));
            if (cacheBodies.HasValue) config.CacheSendCodeBodies = cacheBodies;

            var enableToast = ParseBool(lookup(EnvEnableToast));
            if (enableToast.HasValue) config.EnableToast = enableToast;

            // BIMWRIGHT_UI_LANGUAGE is a Revit.exe process env var (Windows user/machine);
            // the env block in MCP client configs reaches the server, not the plugin.
            var uiLanguage = lookup(EnvUiLanguage);
            if (!string.IsNullOrWhiteSpace(uiLanguage)) config.UiLanguage = uiLanguage.Trim();

            var persistEnv = lookup(EnvPersistSendCodeBodies);
            if (!string.IsNullOrWhiteSpace(persistEnv))
            {
                var val = ParseBool(persistEnv);
                if (val == true)
                {
                    // Already active with a valid Until — do not reset the clock on every Load.
                    // After TTL expiry, sticky env=1 must not revive without explicit CLI enable.
                    if (!config.IsPersistSendCodeBodiesActive()
                        && config.PersistSendCodeBodiesRequiresExplicitEnable != true)
                    {
                        var ttlStr = lookup(EnvPersistSendCodeBodiesTtl);
                        var ttl = TimeSpan.FromHours(config.PersistSendCodeBodiesHoursOrDefault);
                        if (!string.IsNullOrWhiteSpace(ttlStr) && PersistSendCodeTtl.TryParse(ttlStr, out var parsedTtl))
                        {
                            var clamped = PersistSendCodeTtl.Clamp(parsedTtl);
                            if (clamped != parsedTtl)
                                Console.Error.WriteLine($"[rvt-mcp] persist send_code TTL '{ttlStr}' clamped to {FormatTtl(clamped)} (allowed 1h–2d).");
                            ttl = clamped;
                        }
                        var until = DateTimeOffset.UtcNow.Add(ttl);
                        config.PersistSendCodeBodies = true;
                        config.PersistSendCodeBodiesUntil = PersistSendCodeTtl.FormatIsoUntil(until);
                        config.PersistSendCodeBodiesRequiresExplicitEnable = false;
                        if (allowPersistenceWriteBack)
                        {
                            string saveError;
                            TrySavePersistSendCodeBodies(true, until, out saveError, configFilePath);
                        }
                    }
                }
                else if (val == false)
                {
                    config.PersistSendCodeBodies = false;
                    config.PersistSendCodeBodiesUntil = null;
                    config.PersistSendCodeBodiesRequiresExplicitEnable = null;
                    if (allowPersistenceWriteBack)
                    {
                        string clearError;
                        TryClearPersistSendCodeBodies(out clearError, configFilePath, requireExplicitEnable: false);
                    }
                }
            }
        }

        internal static void ApplyCliArgs(RvtMcpConfig config, string[] args)
        {
            ApplyCliArgs(config, args, null);
        }

        internal static void ApplyCliArgs(RvtMcpConfig config, string[] args, string configFilePath)
        {
            if (args == null) return;
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--target":
                        if (i + 1 < args.Length) config.Target = args[++i];
                        break;
                    case "--toolsets":
                        if (i + 1 < args.Length) config.Toolsets = ParseCsv(args[++i]);
                        break;
                    case "--read-only":
                        config.ReadOnly = true;
                        break;
                    case "--enable-send-code": config.EnableSendCode = true; break;
                    case "--disable-send-code": config.EnableSendCode = false; break;
                    case "--enable-call-log": config.EnableCallLog = true; break;
                    case "--disable-call-log": config.EnableCallLog = false; break;
                    case "--enable-change-history": config.EnableChangeHistory = true; break;
                    case "--disable-change-history": config.EnableChangeHistory = false; break;
                    case "--enable-response-guard": config.EnableResponseGuard = true; break;
                    case "--disable-response-guard": config.EnableResponseGuard = false; break;
                    case "--response-warn-bytes": config.ResponseWarnBytes = ReadBytes(args, ref i); break;
                    case "--response-strong-warn-bytes": config.ResponseStrongWarnBytes = ReadBytes(args, ref i); break;
                    case "--response-budget-bytes": config.ResponseBudgetBytes = ReadBytes(args, ref i); break;
                    case "--max-response-bytes": config.MaxResponseBytes = ReadBytes(args, ref i); break;
                    case "--allow-lan-bind":
                        config.AllowLanBind = true;
                        break;
                    case "--enable-toolbaker":
                        config.EnableToolbaker = true;
                        break;
                    case "--disable-toolbaker":
                        config.EnableToolbaker = false;
                        break;
                    case "--enable-adaptive-bake":
                        config.EnableAdaptiveBake = true;
                        break;
                    case "--disable-adaptive-bake":
                        config.EnableAdaptiveBake = false;
                        break;
                    case "--cache-send-code-bodies":
                        config.CacheSendCodeBodies = true;
                        break;
                    case "--no-cache-send-code-bodies":
                        config.CacheSendCodeBodies = false;
                        break;
                    case "--persist-send-code-bodies":
                        {
                            var ttl = TimeSpan.FromHours(config.PersistSendCodeBodiesHoursOrDefault);
                            var until = DateTimeOffset.UtcNow.Add(ttl);
                            config.PersistSendCodeBodies = true;
                            config.PersistSendCodeBodiesUntil = PersistSendCodeTtl.FormatIsoUntil(until);
                            config.PersistSendCodeBodiesRequiresExplicitEnable = false;
                            string saveError;
                            TrySavePersistSendCodeBodies(true, until, out saveError, configFilePath);
                        }
                        break;
                    case "--persist-send-code-bodies-for":
                        if (i + 1 < args.Length)
                        {
                            var ttlStr = args[++i];
                            var ttl = TimeSpan.FromHours(config.PersistSendCodeBodiesHoursOrDefault);
                            if (PersistSendCodeTtl.TryParse(ttlStr, out var parsedTtl))
                            {
                                var clamped = PersistSendCodeTtl.Clamp(parsedTtl);
                                if (clamped != parsedTtl)
                                    Console.Error.WriteLine($"[rvt-mcp] persist send_code TTL '{ttlStr}' clamped to {FormatTtl(clamped)} (allowed 1h–2d).");
                                ttl = clamped;
                            }
                            else
                            {
                                Console.Error.WriteLine($"[rvt-mcp] invalid persist send_code TTL '{ttlStr}'; using default {FormatTtl(PersistSendCodeTtl.Default)}.");
                            }
                            var until = DateTimeOffset.UtcNow.Add(ttl);
                            config.PersistSendCodeBodies = true;
                            config.PersistSendCodeBodiesUntil = PersistSendCodeTtl.FormatIsoUntil(until);
                            config.PersistSendCodeBodiesRequiresExplicitEnable = false;
                            string saveError;
                            TrySavePersistSendCodeBodies(true, until, out saveError, configFilePath);
                        }
                        break;
                    case "--no-persist-send-code-bodies":
                        config.PersistSendCodeBodies = false;
                        config.PersistSendCodeBodiesUntil = null;
                        config.PersistSendCodeBodiesRequiresExplicitEnable = null;
                        string clearError;
                        TryClearPersistSendCodeBodies(out clearError, configFilePath, requireExplicitEnable: false);
                        break;
                }
            }
        }

        private static int ReadBytes(string[] args, ref int index)
        {
            var flag = args[index];
            if (index + 1 >= args.Length)
                throw new ArgumentException(flag + " requires a byte count.");
            return ParseBytes(args[++index], flag) ?? throw new ArgumentException(flag + " requires a byte count.");
        }

        private static int? ParseBytes(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!int.TryParse(value, out var bytes) || bytes < 1024)
                throw new ArgumentException(name + " must be an integer of at least 1024 bytes.");
            return bytes;
        }

        internal static bool? ParseBool(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                    return true;
                case "0":
                case "false":
                case "no":
                    return false;
                default:
                    return null;
            }
        }

        internal static List<string> ParseCsv(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value)) return result;
            foreach (var part in value.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) result.Add(trimmed);
            }
            return result;
        }

        /// <summary>Persisted settings that can be staged by the Settings dialog.</summary>
        public sealed class SettingsPatch
        {
            public int? ToastIdleSeconds { get; set; }
            public int? PersistSendCodeBodiesHours { get; set; }
            public bool? PersistSendCodeBodies { get; set; }
            public bool? CacheSendCodeBodies { get; set; }
        }

        public sealed class ApplyFailure
        {
            public ApplyFailure(string key, string error)
            {
                Key = key;
                Error = error;
            }

            public string Key { get; }
            public string Error { get; }
        }

        /// <summary>Per-key result returned by <see cref="TryApplySettings"/>.</summary>
        public sealed class ApplyResult
        {
            public IList<string> Applied { get; } = new List<string>();
            public IList<ApplyFailure> Failed { get; } = new List<ApplyFailure>();
            public bool JournalWasExpired { get; internal set; }
        }

        /// <summary>
        /// Persist only <c>enableToast</c> into the JSON config file, preserving other keys.
        /// The void wrapper remains for old ribbon callers; new callers should inspect the
        /// boolean and error returned by <see cref="TrySaveEnableToast"/>.
        /// </summary>
        public static void SaveEnableToast(bool enabled, string configFilePath = null)
        {
            string ignoredError;
            TrySaveEnableToast(enabled, out ignoredError, configFilePath);
        }

        public static bool TrySaveEnableToast(bool enabled, out string error, string configFilePath = null)
        {
            return TryUpdateConfig(configFilePath, root => root["enableToast"] = enabled, out error);
        }

        public static bool TrySaveShowBranding(bool show, out string error, string configFilePath = null)
        {
            return TryUpdateConfig(configFilePath, root => root["showBranding"] = show, out error);
        }

        /// <summary>
        /// Lenient read of the toast position keys: a missing, mistyped or non-finite value
        /// falls back to its default instead of failing the configuration.
        /// </summary>
        internal static ToastPositionOptions ReadToastPosition(JToken horizontal, JToken vertical, JToken drag, JToken offset)
        {
            bool Is(JToken token, string value) =>
                token != null && token.Type == JTokenType.String && (string)token == value;
            var box = offset as JObject;
            return new ToastPositionOptions(
                Is(horizontal, "right"),
                Is(vertical, "bottom"),
                drag != null && drag.Type == JTokenType.Boolean && (bool)drag,
                ReadFinite(box?["x"]),
                ReadFinite(box?["y"]));
        }

        private static double? ReadFinite(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                return null;
            try
            {
                var number = token.Value<double>();
                return ToastPositionOptions.Finite(number) ? number : (double?)null;
            }
            catch { return null; }
        }

        /// <summary>Persist only the toast position keys; every other key is kept.</summary>
        public static bool TrySaveToastPosition(ToastPositionOptions options, out string error, string configFilePath = null)
        {
            if (options == null) options = new ToastPositionOptions();
            return TryUpdateConfig(configFilePath, root =>
            {
                root["toastHorizontalAlign"] = options.Right ? "right" : "left";
                root["toastVerticalAlign"] = options.Bottom ? "bottom" : "top";
                root["toastDragEnabled"] = options.DragEnabled;
                if (options.HasOffset)
                    root["toastDragOffset"] = new JObject
                    {
                        ["x"] = options.OffsetX.GetValueOrDefault(),
                        ["y"] = options.OffsetY.GetValueOrDefault()
                    };
                else
                    root.Remove("toastDragOffset");
            }, out error);
        }

        public static bool TrySaveToastIdleSeconds(int seconds, out string error, string configFilePath = null)
        {
            var normalized = NormalizeToastIdleSeconds(seconds);
            return TryUpdateConfig(configFilePath, root => root["toastIdleSeconds"] = normalized, out error);
        }

        /// <summary>
        /// Persist only <c>uiLanguage</c>. The legacy void wrapper is retained for the
        /// ribbon's best-effort behavior; Settings can surface the returned error.
        /// </summary>
        public static void SaveUiLanguage(string code, string configFilePath = null)
        {
            string ignoredError;
            TrySaveUiLanguage(code, out ignoredError, configFilePath);
        }

        public static bool TrySaveUiLanguage(string code, out string error, string configFilePath = null)
        {
            return TryUpdateConfig(configFilePath, root => root["uiLanguage"] = code, out error);
        }

        public static bool TrySavePersistSendCodeBodiesHours(int hours, out string error, string configFilePath = null)
        {
            var normalized = NormalizePersistSendCodeBodiesHours(hours);
            return TryUpdateConfig(configFilePath, root =>
            {
                root["persistSendCodeBodiesHours"] = normalized;
                var now = DateTimeOffset.UtcNow;
                var active = root.Value<bool?>("persistSendCodeBodies") == true
                    && DateTimeOffset.TryParse(root.Value<string>("persistSendCodeBodiesUntil"), out var until)
                    && now < until.ToUniversalTime();
                if (active)
                    root["persistSendCodeBodiesUntil"] = PersistSendCodeTtl.FormatIsoUntil(now.AddHours(normalized));
            }, out error);
        }

        public static void SavePersistSendCodeBodies(bool enabled, DateTimeOffset? untilUtc, string configFilePath = null)
        {
            string ignoredError;
            TrySavePersistSendCodeBodies(enabled, untilUtc, out ignoredError, configFilePath);
        }

        public static bool TrySavePersistSendCodeBodies(
            bool enabled,
            DateTimeOffset? untilUtc,
            out string error,
            string configFilePath = null)
        {
            return TryUpdateConfig(configFilePath, root =>
            {
                root["persistSendCodeBodies"] = enabled;
                if (enabled && untilUtc.HasValue)
                {
                    root["persistSendCodeBodiesUntil"] = PersistSendCodeTtl.FormatIsoUntil(untilUtc.Value);
                    root["persistSendCodeBodiesHours"] = NormalizePersistSendCodeBodiesHours(
                        root.Value<int?>("persistSendCodeBodiesHours"));
                    root.Remove("persistSendCodeBodiesRequiresExplicitEnable");
                }
                else
                {
                    // Turning Off clears only the active Until. The selected duration is
                    // deliberately retained for the next explicit enable.
                    root.Remove("persistSendCodeBodiesUntil");
                }
            }, out error);
        }

        public static void ClearPersistSendCodeBodies(string configFilePath = null, bool requireExplicitEnable = false)
        {
            string ignoredError;
            TryClearPersistSendCodeBodies(out ignoredError, configFilePath, requireExplicitEnable);
        }

        public static bool TryClearPersistSendCodeBodies(
            out string error,
            string configFilePath = null,
            bool requireExplicitEnable = false)
        {
            string path;
            bool explicitPath;
            try
            {
                path = ResolveConfigPath(configFilePath, out explicitPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !IsAllowedConfigDirectory(directory, explicitPath))
            {
                error = "The configuration path is outside the current profile.";
                return false;
            }

            if (!File.Exists(path) && !requireExplicitEnable)
            {
                error = null;
                return true;
            }

            return TryUpdateConfig(configFilePath, root =>
            {
                root.Remove("persistSendCodeBodies");
                root.Remove("persistSendCodeBodiesUntil");
                if (requireExplicitEnable)
                    root["persistSendCodeBodiesRequiresExplicitEnable"] = true;
                else
                    root.Remove("persistSendCodeBodiesRequiresExplicitEnable");
            }, out error);
        }

        /// <summary>
        /// Apply the Settings dialog's staged values one key at a time. Each operation
        /// rereads the file immediately before writing, so an external edit made while
        /// the dialog was open is merged and the staged key wins. A failed key does not
        /// roll back a key that already succeeded.
        /// </summary>
        public static ApplyResult TryApplySettings(SettingsPatch patch, string configFilePath = null)
        {
            var result = new ApplyResult();
            if (patch == null)
            {
                result.Failed.Add(new ApplyFailure("settings", "Settings patch is required."));
                return result;
            }

            var toastIdleApplied = false;
            if (patch.ToastIdleSeconds.HasValue)
            {
                string error;
                if (TrySaveToastIdleSeconds(patch.ToastIdleSeconds.Value, out error, configFilePath))
                {
                    result.Applied.Add("toastIdleSeconds");
                    toastIdleApplied = true;
                }
                else
                {
                    result.Failed.Add(new ApplyFailure("toastIdleSeconds", error));
                }
            }

            if (patch.PersistSendCodeBodiesHours.HasValue
                || patch.PersistSendCodeBodies.HasValue
                || patch.CacheSendCodeBodies.HasValue)
            {
                string error;
                var ok = TryUpdateConfig(configFilePath, root =>
                {
                    // If another process edits the file between the per-key writes,
                    // restage the first key so every staged value still wins.
                    if (toastIdleApplied)
                        root["toastIdleSeconds"] = NormalizeToastIdleSeconds(patch.ToastIdleSeconds);

                    if (patch.CacheSendCodeBodies.HasValue)
                        root["cacheSendCodeBodies"] = patch.CacheSendCodeBodies.Value;

                    var now = DateTimeOffset.UtcNow;
                    var currentHours = NormalizePersistSendCodeBodiesHours(root.Value<int?>("persistSendCodeBodiesHours"));
                    var hours = patch.PersistSendCodeBodiesHours.HasValue
                        ? NormalizePersistSendCodeBodiesHours(patch.PersistSendCodeBodiesHours.Value)
                        : currentHours;
                    var currentEnabled = root.Value<bool?>("persistSendCodeBodies") == true;
                    var currentActive = currentEnabled
                        && DateTimeOffset.TryParse(root.Value<string>("persistSendCodeBodiesUntil"), out var until)
                        && now < until.ToUniversalTime();

                    if (patch.PersistSendCodeBodiesHours.HasValue)
                        root["persistSendCodeBodiesHours"] = hours;
                    if (patch.PersistSendCodeBodies.HasValue)
                    {
                        var enabled = patch.PersistSendCodeBodies.Value;
                        root["persistSendCodeBodies"] = enabled;
                        if (enabled)
                        {
                            root["persistSendCodeBodiesUntil"] = PersistSendCodeTtl.FormatIsoUntil(now.AddHours(hours));
                            root.Remove("persistSendCodeBodiesRequiresExplicitEnable");
                        }
                        else
                        {
                            root.Remove("persistSendCodeBodiesUntil");
                            root.Remove("persistSendCodeBodiesRequiresExplicitEnable");
                        }
                    }
                    else if (patch.PersistSendCodeBodiesHours.HasValue && currentActive)
                    {
                        // A duration edit re-arms an active journal from Apply time. An
                        // already expired journal is left expired and is never revived by
                        // changing an unrelated staged value.
                        root["persistSendCodeBodiesUntil"] = PersistSendCodeTtl.FormatIsoUntil(now.AddHours(hours));
                    }
                    else if (patch.PersistSendCodeBodiesHours.HasValue && currentEnabled)
                    {
                        result.JournalWasExpired = true;
                    }
                }, out error);

                if (ok && patch.PersistSendCodeBodiesHours.HasValue)
                    result.Applied.Add("persistSendCodeBodiesHours");
                else if (!ok && patch.PersistSendCodeBodiesHours.HasValue)
                    result.Failed.Add(new ApplyFailure("persistSendCodeBodiesHours", error));

                if (patch.PersistSendCodeBodies.HasValue && ok)
                    result.Applied.Add("persistSendCodeBodies");
                else if (patch.PersistSendCodeBodies.HasValue && !ok)
                    result.Failed.Add(new ApplyFailure("persistSendCodeBodies", error));

                if (patch.CacheSendCodeBodies.HasValue && ok)
                    result.Applied.Add("cacheSendCodeBodies");
                else if (patch.CacheSendCodeBodies.HasValue && !ok)
                    result.Failed.Add(new ApplyFailure("cacheSendCodeBodies", error));
            }

            return result;
        }

        /// <summary>
        /// Read the existing config root for a single-key update. Returns false when the
        /// file exists but cannot be read or parsed — a torn read (e.g. another Revit
        /// instance mid-write) must never be treated as an empty config, or the write
        /// would silently drop readOnly and every other setting.
        /// </summary>
        private static bool TryReadConfigRoot(string path, out JObject root)
        {
            root = new JObject();
            if (!File.Exists(path)) return true;
            try
            {
                using (var textReader = new StringReader(File.ReadAllText(path)))
                using (var jsonReader = new JsonTextReader(textReader)
                {
                    // Until is a persisted string. Do not let Newtonsoft turn it into
                    // a local DateTime and then rewrite it with a different offset.
                    DateParseHandling = DateParseHandling.None,
                })
                {
                    var token = JToken.ReadFrom(jsonReader);
                    root = token as JObject;
                    if (root == null)
                    {
                        root = null;
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                root = null;
                return false;
            }
        }

        private static readonly object ConfigWriteGate = new object();

        // Settings and the toast thread (drag release) can both save: serialize the
        // read-modify-write so one cannot overwrite the other's key.
        private static bool TryUpdateConfig(string configFilePath, Action<JObject> update, out string error)
        {
            lock (ConfigWriteGate)
                return TryUpdateConfigCore(configFilePath, update, out error);
        }

        private static bool TryUpdateConfigCore(string configFilePath, Action<JObject> update, out string error)
        {
            error = null;
            string tempPath = null;
            try
            {
                var path = ResolveConfigPath(configFilePath, out var explicitPath);
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory) || !IsAllowedConfigDirectory(directory, explicitPath))
                {
                    error = "The configuration path is outside the current profile.";
                    return false;
                }

                JObject root;
                if (!TryReadConfigRoot(path, out root))
                {
                    error = "The configuration file is not valid JSON.";
                    return false;
                }

                update(root);
                Directory.CreateDirectory(directory);
                var json = root.ToString(Formatting.Indented) + Environment.NewLine;
                tempPath = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                var bytes = Encoding.UTF8.GetBytes(json);
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                    File.Replace(tempPath, path, destinationBackupFileName: null);
                else
                    File.Move(tempPath, path);
                tempPath = null;
                return true;
            }
            catch (Exception ex)
            {
                // The caller logs the diagnostic; UI surfaces a localized generic error.
                error = ex.Message;
                return false;
            }
            finally
            {
                if (tempPath != null)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch { }
                }
            }
        }

        private static string ResolveConfigPath(string configFilePath, out bool explicitPath)
        {
            explicitPath = !string.IsNullOrWhiteSpace(configFilePath) || !string.IsNullOrWhiteSpace(ConfigFilePathOverride);
            var selected = string.IsNullOrWhiteSpace(configFilePath)
                ? (ConfigFilePathOverride ?? DefaultConfigFilePath)
                : configFilePath;
            return Path.GetFullPath(selected);
        }

        private static bool IsAllowedConfigDirectory(string directory, bool explicitPath)
        {
            var profileRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");
            if (IsPathWithin(directory, profileRoot))
                return true;

            // Explicit temp paths are the supported test seam. Production calls use the
            // default profile path; no arbitrary current-working-directory writes.
            return explicitPath && IsPathWithin(directory, Path.GetTempPath());
        }

        private static bool IsPathWithin(string candidate, string root)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
                return false;
            var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
                return true;
            return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullCandidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatTtl(TimeSpan ttl)
        {
            if (ttl.TotalDays >= 1 && Math.Abs(ttl.TotalDays - Math.Round(ttl.TotalDays)) < 0.001)
                return $"{(int)Math.Round(ttl.TotalDays)}d";
            if (ttl.TotalHours >= 1 && Math.Abs(ttl.TotalHours - Math.Round(ttl.TotalHours)) < 0.001)
                return $"{(int)Math.Round(ttl.TotalHours)}h";
            return $"{(int)Math.Round(ttl.TotalMinutes)}m";
        }
    }
}
