using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace RvtMcp.Scripting
{
    public sealed class ScriptModuleRevision
    {
        public string Name { get; }
        public string Code { get; }
        public string CodeSha256 { get; }
        public string Hash { get; }
        public ReadOnlyCollection<string> Requires { get; }

        public ScriptModuleRevision(string name, string code, IEnumerable<string> requires = null)
        {
            ValidateName(name);
            if (string.IsNullOrWhiteSpace(code))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Module source is required.");
            Name = name;
            Code = ScriptIdentity.NormalizeSource(code);
            if (ScriptIdentity.Utf8Bytes(Code) > ScriptLimits.ModuleBytes)
                throw new ScriptContractException(ScriptErrorCodes.ModuleLimitExceeded, "Module source exceeds its UTF-8 budget.");
            Requires = ValidateDependencies(name, requires);
            CodeSha256 = ScriptIdentity.CodeSha256(Code);
            Hash = ScriptIdentity.ModuleRevisionHash(Name, CodeSha256, Requires);
        }

        internal static void ValidateName(string name)
        {
            if (name == null || !Regex.IsMatch(name, "\\A[a-z][a-z0-9_]{0,63}\\z") ||
                Regex.IsMatch(name, "\\A(con|prn|aux|nul|com[1-9]|lpt[1-9])\\z"))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Invalid script module name.");
        }

        internal static ReadOnlyCollection<string> ValidateDependencies(string name, IEnumerable<string> requires)
        {
            var dependencies = (requires ?? Enumerable.Empty<string>()).ToArray();
            foreach (var dependency in dependencies) ValidateName(dependency);
            if (dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Length)
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Duplicate module dependencies are not allowed.");
            if (dependencies.Contains(name, StringComparer.Ordinal))
                throw new ScriptContractException(ScriptErrorCodes.ModuleCycle, "A module cannot depend on itself.");
            return Array.AsReadOnly(dependencies);
        }
    }

    /// <summary>Ordered, resolved identity only. Dependency resolution and persistence belong to P05.</summary>
    public sealed class ScriptModuleManifestEntry
    {
        public string Name { get; }
        public string Hash { get; }
        public ReadOnlyCollection<string> Requires { get; }

        public ScriptModuleManifestEntry(string name, string hash, IEnumerable<string> requires = null)
        {
            ScriptModuleRevision.ValidateName(name);
            if (hash == null || !Regex.IsMatch(hash, "\\A[a-fA-F0-9]{64}\\z"))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Invalid module revision hash.");
            Name = name;
            Hash = hash.ToLowerInvariant();
            Requires = ScriptModuleRevision.ValidateDependencies(name, requires);
        }
    }
}
