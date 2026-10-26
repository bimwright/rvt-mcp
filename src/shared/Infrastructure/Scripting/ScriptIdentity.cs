using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Scripting
{
    public static class ScriptIdentity
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static string NormalizeSource(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return source.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        public static int Utf8Bytes(string value) => Utf8.GetByteCount(value);

        public static string CodeSha256(string code) => Sha256(NormalizeSource(code));

        public static string Sha256(string value)
        {
            using (var algorithm = SHA256.Create())
                return string.Concat(algorithm.ComputeHash(Utf8.GetBytes(value)).Select(b => b.ToString("x2")));
        }

        public static string CanonicalHash(string json) => Sha256(ScriptJson.Parse(json).CanonicalJson);

        public static string ModuleRevisionHash(string name, string codeSha256, IEnumerable<string> requires)
        {
            var identity = new ScriptModuleManifestEntry(name, codeSha256, requires);
            return HashIdentity(new JObject
            {
                ["identity_version"] = ScriptLimits.IdentityVersion,
                ["name"] = identity.Name,
                ["code_sha256"] = identity.Hash,
                ["requires"] = new JArray(identity.Requires)
            });
        }

        public static string ManifestHash(IEnumerable<ScriptModuleManifestEntry> manifest)
            => HashIdentity(Manifest(manifest));

        /// <summary>Only the phase-1 source identity; no persisted ToolBaker descriptor/schema revision.</summary>
        public static string ScriptKey(string code, IEnumerable<ScriptModuleManifestEntry> manifest,
            string entrypointMode, string resultContract)
        {
            ScriptModes.Check(entrypointMode, ScriptModes.App, ScriptModes.AppData);
            ScriptModes.Check(resultContract, ScriptModes.Legacy, ScriptModes.TaskV1);
            return "script_v1:" + HashIdentity(new JObject
            {
                ["identity_version"] = ScriptLimits.IdentityVersion,
                ["script_code_sha256"] = CodeSha256(code),
                ["module_manifest"] = Manifest(manifest),
                ["entrypoint_mode"] = entrypointMode,
                ["result_contract"] = resultContract
            });
        }

        private static JArray Manifest(IEnumerable<ScriptModuleManifestEntry> manifest)
        {
            var entries = (manifest ?? Enumerable.Empty<ScriptModuleManifestEntry>()).ToArray();
            if (entries.Length > ScriptLimits.ModuleCount)
                throw new ScriptContractException(ScriptErrorCodes.ModuleLimitExceeded, "Too many resolved modules.");
            if (entries.Any(e => e == null) || entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != entries.Length)
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Invalid resolved module manifest.");
            return new JArray(entries.Select(e => new JObject
            {
                ["name"] = e.Name, ["hash"] = e.Hash, ["requires"] = new JArray(e.Requires)
            }));
        }

        private static string HashIdentity(JToken identity) => CanonicalHash(identity.ToString(Formatting.None));
    }
}
