#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin;

/// <summary>Loaded assembly identity plus a scoped disk observation, not installation or publisher verification.</summary>
public static class RuntimeIdentity
{
#if NET8_0_OR_GREATER
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty Assembly.Location is expected; the gateway entry assembly falls back to the running executable.")]
#endif
    public static JObject Capture(Assembly assembly, string component, int? hostYear = null)
    {
        if (component != "gateway" && component != "plugin")
            throw new ArgumentException("Unknown runtime component.", nameof(component));
        using var process = Process.GetCurrentProcess();
        var capturedAt = DateTimeOffset.UtcNow.ToString("o");
        var location = assembly.Location;
        // Single-file gateways have no managed Assembly.Location. Never hash the host executable as a plugin.
        if (string.IsNullOrEmpty(location) && component == "gateway" && assembly == Assembly.GetEntryAssembly())
            location = process.MainModule?.FileName;
        string? hash = null;
        if (!string.IsNullOrEmpty(location))
        {
            try
            {
                using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var sha = SHA256.Create();
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { /* A missing/replaced disk file must not hide the loaded assembly identity. */ }
        }
        return new JObject
        {
            ["schema_version"] = 1,
            ["product_id"] = "rvt-mcp",
            ["component"] = component,
            ["host_year"] = hostYear,
            ["process_id"] = process.Id,
            ["process_started_utc"] = process.StartTime.ToUniversalTime().ToString("o"),
            ["identity_captured_utc"] = capturedAt,
            ["identity_basis"] = "loaded_assembly",
            ["assembly_informational_version"] = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["module_version_id"] = assembly.ManifestModule.ModuleVersionId.ToString("D"),
            ["artifact_file"] = string.IsNullOrEmpty(location) ? null : Path.GetFileName(location),
            ["artifact_sha256"] = hash,
            ["artifact_hash_basis"] = "on_disk_at_identity_capture",
            ["publisher_verified"] = false,
        };
    }
}
