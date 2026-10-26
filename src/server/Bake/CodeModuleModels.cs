using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Scripting;

namespace RvtMcp.Server.Bake
{
    internal sealed class CodeModuleCompilation
    {
        internal int HostYear { get; }
        internal string RuntimeIdentity { get; }
        internal ReadOnlyCollection<string> Signatures { get; }
        internal CodeModuleCompilation(int hostYear, string runtimeIdentity, IEnumerable<string> signatures = null)
        {
            if (hostYear < 2022 || hostYear > 2027 || string.IsNullOrWhiteSpace(runtimeIdentity))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "A verified compile host and runtime identity are required.");
            HostYear = hostYear; RuntimeIdentity = runtimeIdentity;
            Signatures = Array.AsReadOnly((signatures ?? Enumerable.Empty<string>()).ToArray());
            if (Signatures.Any(s => s == null)) throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Invalid compile signatures.");
        }
        internal JObject Host(string manifestHash) => new JObject { ["host_year"] = HostYear, ["runtime_identity"] = RuntimeIdentity,
            ["compile_verified"] = true, ["native_verified"] = false, ["runtime_observed"] = false, ["module_manifest_hash"] = manifestHash };
    }

    internal sealed class CodeModuleRecord
    {
        internal ScriptModuleRevision Revision { get; }
        internal string Description { get; }
        internal string SignaturesJson { get; }
        internal string CompiledHostJson { get; }
        internal string UpdatedAt { get; }
        internal CodeModuleRecord(ScriptModuleRevision revision, string description, string signatures, string host, string updatedAt)
        { Revision = revision; Description = description; SignaturesJson = signatures; CompiledHostJson = host; UpdatedAt = updatedAt; }
    }

    internal sealed class CodeModuleSnapshot
    {
        internal ReadOnlyCollection<string> Roots { get; }
        internal ReadOnlyCollection<ScriptModuleRevision> Modules { get; }
        internal string ManifestHash { get; }
        internal CodeModuleSnapshot(IEnumerable<string> roots, IEnumerable<ScriptModuleRevision> modules)
        {
            Roots = Array.AsReadOnly(roots.ToArray()); Modules = Array.AsReadOnly(modules.ToArray());
            ManifestHash = ScriptIdentity.ManifestHash(Modules.Select(m => new ScriptModuleManifestEntry(m.Name, m.Hash, m.Requires)));
        }
    }

    internal sealed class CodeModuleSaveResult
    {
        internal CodeModuleRecord Record { get; }
        internal bool Changed { get; }
        internal CodeModuleCompilation Validation { get; }
        internal string ValidationManifestHash { get; }
        internal CodeModuleSaveResult(CodeModuleRecord record, bool changed, CodeModuleCompilation validation, string validationManifestHash)
        { Record = record; Changed = changed; Validation = validation; ValidationManifestHash = validationManifestHash; }
    }

    internal sealed class CodeModuleListItem
    {
        internal CodeModuleRecord Record { get; }
        internal string DependencyStatus { get; }
        internal CodeModuleListItem(CodeModuleRecord record, string dependencyStatus) { Record = record; DependencyStatus = dependencyStatus; }
    }

    internal sealed class CodeModulePage
    {
        internal ReadOnlyCollection<CodeModuleListItem> Items { get; }
        internal int TotalCount { get; }
        internal CodeModulePage(IEnumerable<CodeModuleListItem> items, int totalCount) { Items = Array.AsReadOnly(items.ToArray()); TotalCount = totalCount; }
    }
}
