using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Localization
{
    /// <summary>
    /// Validates per-user override files against the embedded en catalog (spec §7.2).
    /// A rejected file/entry is never applied — the previous table stays live.
    /// </summary>
    public static class OverrideValidator
    {
        public const long MaxFileBytes = 1024 * 1024;
        public const int MaxValueLength = 1000;

        public sealed class Result
        {
            public Dictionary<string, string> Accepted { get; } =
                new Dictionary<string, string>(StringComparer.Ordinal);
            public List<KeyValuePair<string, string>> Rejected { get; } =
                new List<KeyValuePair<string, string>>();
            /// <summary>"invalid_json" | "file_too_large" | null.</summary>
            public string FileError { get; set; }
        }

        /// <summary>Missing/unreadable-but-absent file → empty result, no error.</summary>
        public static Result ValidateFile(string path, IReadOnlyDictionary<string, string> en)
        {
            string json;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return new Result();
                if (fi.Length > MaxFileBytes)
                    return new Result { FileError = "file_too_large" };
                json = File.ReadAllText(path);
            }
            catch
            {
                return new Result { FileError = "invalid_json" };
            }
            return ValidateJson(json, en);
        }

        public static Result ValidateJson(string json, IReadOnlyDictionary<string, string> en)
        {
            var result = new Result();
            JObject root;
            try { root = JObject.Parse(json); }
            catch
            {
                result.FileError = "invalid_json";
                return result;
            }

            foreach (var prop in root.Properties())
            {
                var key = prop.Name;
                if (key == "_meta") continue;   // optional, ignored — not unknown_key
                if (prop.Value.Type != JTokenType.String)
                {
                    result.Rejected.Add(new KeyValuePair<string, string>(key, "not_a_string"));
                    continue;
                }
                var value = prop.Value.Value<string>();
                if (en == null || !en.ContainsKey(key))
                {
                    result.Rejected.Add(new KeyValuePair<string, string>(key, "unknown_key"));
                    continue;
                }
                if (key.StartsWith(StringTable.LockedPrefix, StringComparison.Ordinal))
                {
                    result.Rejected.Add(new KeyValuePair<string, string>(key, "locked_key"));
                    continue;
                }
                if (value != null && value.Length > MaxValueLength)
                {
                    result.Rejected.Add(new KeyValuePair<string, string>(key, "value_too_long"));
                    continue;
                }
                if (!TokensMatch(en[key], value))
                {
                    result.Rejected.Add(new KeyValuePair<string, string>(key, "placeholder_mismatch"));
                    continue;
                }
                result.Accepted[key] = value;
            }
            return result;
        }

        private static bool TokensMatch(string enTemplate, string overrideValue)
        {
            var enTokens = StringTable.ExtractTokens(enTemplate);
            var ovTokens = StringTable.ExtractTokens(overrideValue);
            if (enTokens.Count != ovTokens.Count) return false;
            foreach (var t in enTokens)
                if (!ovTokens.Contains(t)) return false;
            return true;
        }
    }
}
