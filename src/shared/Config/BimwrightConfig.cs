using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin
{
    /// <summary>
    /// A9 3-layer config (aspect #3 §A9). Single POCO read by both processes:
    /// Server consumes <see cref="Target"/> / <see cref="Toolsets"/> / <see cref="ReadOnly"/>;
    /// Plugin consumes <see cref="AllowLanBind"/> / <see cref="EnableToolbaker"/>.
    ///
    /// Precedence (high → low): CLI args > env vars (BIMWRIGHT_*) > JSON file.
    /// Fields stay nullable so "not set" is distinguishable from "explicitly default-valued";
    /// resolved defaults are exposed via the *OrDefault accessors.
    /// </summary>
    public class BimwrightConfig
    {
        public const string EnvTarget            = "BIMWRIGHT_TARGET";
        public const string EnvToolsets          = "BIMWRIGHT_TOOLSETS";
        public const string EnvReadOnly          = "BIMWRIGHT_READ_ONLY";
        public const string EnvAllowLanBind      = "BIMWRIGHT_ALLOW_LAN_BIND";
        public const string EnvEnableToolbaker   = "BIMWRIGHT_ENABLE_TOOLBAKER";

        public const bool DefaultReadOnly        = false;
        public const bool DefaultAllowLanBind    = false;
        public const bool DefaultEnableToolbaker = true;

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

        public bool ReadOnlyOrDefault        => ReadOnly        ?? DefaultReadOnly;
        public bool AllowLanBindOrDefault    => AllowLanBind    ?? DefaultAllowLanBind;
        public bool EnableToolbakerOrDefault => EnableToolbaker ?? DefaultEnableToolbaker;

        public static string DefaultConfigFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright",
                "bimwright.config.json");

        /// <summary>
        /// Load config from JSON → overlay env vars → overlay CLI args. Pass <c>null</c>
        /// for args to skip the CLI layer (plugin-process callers do this since Revit
        /// does not propagate Server args).
        /// </summary>
        public static BimwrightConfig Load(string[] args = null, string configFilePath = null)
        {
            return Load(args, configFilePath, envLookup: null);
        }

        internal static BimwrightConfig Load(string[] args, string configFilePath, Func<string, string> envLookup)
        {
            var config = LoadFromJsonFile(configFilePath ?? DefaultConfigFilePath)
                         ?? new BimwrightConfig();
            ApplyEnvVars(config, envLookup);
            if (args != null) ApplyCliArgs(config, args);
            return config;
        }

        internal static BimwrightConfig LoadFromJsonFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            try
            {
                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text)) return null;
                return JsonConvert.DeserializeObject<BimwrightConfig>(text);
            }
            catch
            {
                // Malformed config = ignore silently, fall back to env/CLI + code defaults.
                // Don't punish the user for a typo in a file that's optional.
                return null;
            }
        }

        internal static void ApplyEnvVars(BimwrightConfig config, Func<string, string> lookup = null)
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
        }

        internal static void ApplyCliArgs(BimwrightConfig config, string[] args)
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
    }
}
