using System;
using System.Collections.Generic;
using System.IO;
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

        public const bool DefaultReadOnly                  = false;
        public const bool DefaultAllowLanBind              = false;
        public const bool DefaultEnableToolbaker           = true;
        public const bool DefaultEnableAdaptiveBake        = false;
        public const bool DefaultCacheSendCodeBodies       = false;
        public const bool DefaultEnableToast               = true;
        public const bool DefaultPersistSendCodeBodies     = false;

        [JsonProperty("target")]
        public string Target { get; set; }

        [JsonProperty("toolsets")]
        public List<string> Toolsets { get; set; }

        [JsonProperty("readOnly")]
        public bool? ReadOnly { get; set; }

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

        [JsonProperty("persistSendCodeBodies")]
        public bool? PersistSendCodeBodies { get; set; }

        [JsonProperty("persistSendCodeBodiesUntil")]
        public string PersistSendCodeBodiesUntil { get; set; }

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
        public bool AllowLanBindOrDefault          => AllowLanBind       ?? DefaultAllowLanBind;
        public bool EnableToolbakerOrDefault       => EnableToolbaker    ?? DefaultEnableToolbaker;
        public bool EnableAdaptiveBakeOrDefault    => EnableAdaptiveBake ?? DefaultEnableAdaptiveBake;
        public bool CacheSendCodeBodiesOrDefault  => CacheSendCodeBodies ?? DefaultCacheSendCodeBodies;
        public bool EnableToastOrDefault          => EnableToast       ?? DefaultEnableToast;

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
                "RvtMcp",
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
                    ClearPersistSendCodeBodies(path, requireExplicitEnable: true);
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
            ApplyEnvVars(config, lookup, null);
        }

        internal static void ApplyEnvVars(RvtMcpConfig config, Func<string, string> lookup, string configFilePath)
        {
            lookup = lookup ?? Environment.GetEnvironmentVariable;

            var target = lookup(EnvTarget);
            if (!string.IsNullOrWhiteSpace(target)) config.Target = target.Trim();

            var toolsets = lookup(EnvToolsets);
            if (!string.IsNullOrWhiteSpace(toolsets)) config.Toolsets = ParseCsv(toolsets);

            var readOnly = ParseBool(lookup(EnvReadOnly));
            if (readOnly.HasValue) config.ReadOnly = readOnly;

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
                        var ttl = PersistSendCodeTtl.Default;
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
                        SavePersistSendCodeBodies(true, until, configFilePath);
                    }
                }
                else if (val == false)
                {
                    config.PersistSendCodeBodies = false;
                    config.PersistSendCodeBodiesUntil = null;
                    config.PersistSendCodeBodiesRequiresExplicitEnable = null;
                    ClearPersistSendCodeBodies(configFilePath, requireExplicitEnable: false);
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
                            var ttl = PersistSendCodeTtl.Default;
                            var until = DateTimeOffset.UtcNow.Add(ttl);
                            config.PersistSendCodeBodies = true;
                            config.PersistSendCodeBodiesUntil = PersistSendCodeTtl.FormatIsoUntil(until);
                            config.PersistSendCodeBodiesRequiresExplicitEnable = false;
                            SavePersistSendCodeBodies(true, until, configFilePath);
                        }
                        break;
                    case "--persist-send-code-bodies-for":
                        if (i + 1 < args.Length)
                        {
                            var ttlStr = args[++i];
                            var ttl = PersistSendCodeTtl.Default;
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
                            SavePersistSendCodeBodies(true, until, configFilePath);
                        }
                        break;
                    case "--no-persist-send-code-bodies":
                        config.PersistSendCodeBodies = false;
                        config.PersistSendCodeBodiesUntil = null;
                        config.PersistSendCodeBodiesRequiresExplicitEnable = null;
                        ClearPersistSendCodeBodies(configFilePath, requireExplicitEnable: false);
                        break;
                }
            }
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

        /// <summary>
        /// Persist only <c>enableToast</c> into the JSON config file, preserving other keys.
        /// Used by the ribbon toggle so the preference survives Revit restarts.
        /// </summary>
        public static void SaveEnableToast(bool enabled, string configFilePath = null)
        {
            var path = string.IsNullOrWhiteSpace(configFilePath) ? DefaultConfigFilePath : configFilePath;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                JObject root;
                if (File.Exists(path))
                {
                    try
                    {
                        root = JObject.Parse(File.ReadAllText(path)) ?? new JObject();
                    }
                    catch
                    {
                        root = new JObject();
                    }
                }
                else
                {
                    root = new JObject();
                }

                root["enableToast"] = enabled;
                File.WriteAllText(path, root.ToString(Formatting.Indented));
            }
            catch
            {
                // Best-effort — toggle still works in-memory for this session.
            }
        }

        /// <summary>
        /// Persist only <c>uiLanguage</c> into the JSON config file, preserving other keys.
        /// Used by the ribbon Language combo so the preference survives Revit restarts.
        /// </summary>
        public static void SaveUiLanguage(string code, string configFilePath = null)
        {
            var path = string.IsNullOrWhiteSpace(configFilePath) ? DefaultConfigFilePath : configFilePath;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                JObject root;
                if (File.Exists(path))
                {
                    try
                    {
                        root = JObject.Parse(File.ReadAllText(path)) ?? new JObject();
                    }
                    catch
                    {
                        root = new JObject();
                    }
                }
                else
                {
                    root = new JObject();
                }

                root["uiLanguage"] = code;
                File.WriteAllText(path, root.ToString(Formatting.Indented));
            }
            catch
            {
                // Best-effort — language still changes in-memory for this session.
            }
        }

        public static void SavePersistSendCodeBodies(bool enabled, DateTimeOffset? untilUtc, string configFilePath = null)
        {
            var path = string.IsNullOrWhiteSpace(configFilePath) ? DefaultConfigFilePath : configFilePath;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                JObject root;
                if (File.Exists(path))
                {
                    try
                    {
                        root = JObject.Parse(File.ReadAllText(path)) ?? new JObject();
                    }
                    catch
                    {
                        root = new JObject();
                    }
                }
                else
                {
                    root = new JObject();
                }

                root["persistSendCodeBodies"] = enabled;
                if (enabled && untilUtc.HasValue)
                {
                    root["persistSendCodeBodiesUntil"] = PersistSendCodeTtl.FormatIsoUntil(untilUtc.Value);
                    root.Remove("persistSendCodeBodiesRequiresExplicitEnable");
                }
                else
                {
                    root.Remove("persistSendCodeBodiesUntil");
                }
                File.WriteAllText(path, root.ToString(Formatting.Indented));
            }
            catch
            {
                // Best-effort
            }
        }

        public static void ClearPersistSendCodeBodies(string configFilePath = null, bool requireExplicitEnable = false)
        {
            var path = string.IsNullOrWhiteSpace(configFilePath) ? DefaultConfigFilePath : configFilePath;
            try
            {
                if (!File.Exists(path) && !requireExplicitEnable) return;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                JObject root;
                if (File.Exists(path))
                {
                    try
                    {
                        root = JObject.Parse(File.ReadAllText(path)) ?? new JObject();
                    }
                    catch
                    {
                        root = new JObject();
                    }
                }
                else
                {
                    root = new JObject();
                }

                root.Remove("persistSendCodeBodies");
                root.Remove("persistSendCodeBodiesUntil");
                if (requireExplicitEnable)
                    root["persistSendCodeBodiesRequiresExplicitEnable"] = true;
                else
                    root.Remove("persistSendCodeBodiesRequiresExplicitEnable");

                File.WriteAllText(path, root.ToString(Formatting.Indented));
            }
            catch
            {
                // Best-effort
            }
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
