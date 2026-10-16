#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using RvtMcp.Plugin;

namespace RvtMcp.Tests;

public sealed class RuntimeIdentityTests
{
    [Theory]
    [InlineData("gateway", null)]
    [InlineData("plugin", 2025)]
    public void Identity_describes_loaded_assembly_and_scopes_hash_to_the_observed_disk_file(string component, int? hostYear)
    {
        var assembly = typeof(RuntimeIdentity).Assembly;
        var data = RuntimeIdentity.Capture(assembly, component, hostYear);
        Assert.Equal("rvt-mcp", data.Value<string>("product_id"));
        Assert.Equal(component, data.Value<string>("component"));
        Assert.Equal(hostYear, data.Value<int?>("host_year"));
        Assert.Equal(Environment.ProcessId, data.Value<int>("process_id"));
        Assert.True(DateTimeOffset.TryParse(data.Value<string>("process_started_utc"), out _));
        Assert.True(DateTimeOffset.TryParse(data.Value<string>("identity_captured_utc"), out _));
        Assert.Equal("loaded_assembly", data.Value<string>("identity_basis"));
        Assert.Equal(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            data.Value<string>("assembly_informational_version"));
        Assert.Equal(assembly.ManifestModule.ModuleVersionId.ToString("D"), data.Value<string>("module_version_id"));
        Assert.Equal(Path.GetFileName(assembly.Location), data.Value<string>("artifact_file"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant(),
            data.Value<string>("artifact_sha256"));
        Assert.Equal("on_disk_at_identity_capture", data.Value<string>("artifact_hash_basis"));
        Assert.False(data.Value<bool>("publisher_verified"));
        Assert.DoesNotContain(Path.GetDirectoryName(assembly.Location)!, data.ToString());
    }

    [Fact]
    public void Plugin_without_a_disk_location_never_hashes_the_host_executable()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("in-memory-plugin"), AssemblyBuilderAccess.Run);
        assembly.DefineDynamicModule("plugin");
        var data = RuntimeIdentity.Capture(assembly, "plugin", 2025);
        Assert.Null(data.Value<string>("artifact_file"));
        Assert.Null(data.Value<string>("artifact_sha256"));
        Assert.Null(data.Value<string>("assembly_informational_version"));
        Assert.NotNull(data.Value<string>("module_version_id"));
    }

    [Fact]
    public void Unknown_component_is_rejected()
        => Assert.Throws<ArgumentException>(() => RuntimeIdentity.Capture(typeof(RuntimeIdentity).Assembly, "host"));
}
