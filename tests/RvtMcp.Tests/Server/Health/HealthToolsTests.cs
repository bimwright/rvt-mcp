using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class HealthToolsTests : IDisposable
{
    private readonly Func<string, object, int?, Task<JObject>> _originalSend = ToolGateway.SendOverride;
    private static JObject Identity(string revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") => new()
    {
        ["assembly_informational_version"] = "1.1.0+" + revision,
        ["module_version_id"] = "c34ec798-b983-4dc8-bfff-056f9b523d3c"
    };
    private static JObject Data(CallToolResult result)
        => JObject.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);

    [Fact]
    public async Task Offline_health_keeps_busy_state_loaded_identity_and_alignment_without_http()
    {
        ToolGateway.SendOverride = (command, args, timeout) =>
        {
            Assert.Equal("health", command);
            Assert.Equal(10, timeout);
            Assert.Empty(JObject.FromObject(args).Properties());
            return Task.FromResult(new JObject
            {
                ["ok"] = true, ["waiting_for_user"] = true, ["ready_for_commands"] = false,
                ["state_probe"] = "last_ui_observation", ["has_active_document"] = false,
                ["setup_plugin"] = Identity("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            });
        };
        var handler = new ReleaseUpdateCheckTests.Handler();
        using var http = new HttpClient(handler);
        var result = await HealthTools.Probe(false, default, new ReleaseUpdateCheck(http), Identity());
        var health = Data(result);
        Assert.False(result.IsError);
        Assert.True(health.Value<bool>("host_connected"));
        Assert.True(health.Value<bool>("waiting_for_user"));
        Assert.False(health.Value<bool>("ready_for_commands"));
        Assert.False(health.Value<bool>("has_active_document"));
        Assert.Equal("match", health["mcp"].Value<string>("version_alignment"));
        Assert.Equal("mismatch", health["mcp"].Value<string>("revision_alignment"));
        Assert.Equal("not_checked", health["update"].Value<string>("status"));
        Assert.Equal("check_only", health["update_guidance"].Value<string>("mode"));
        Assert.Equal("not_observable_by_server", health["client_verification"].Value<string>("status"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("Unknown command: health")]
    [InlineData("The add-in loaded in Revit 2024 does not know the command 'health'.")]
    public async Task Legacy_listener_does_not_inherit_gateway_identity_or_offer_installation(string error)
    {
        ToolGateway.SendOverride = (_, _, _) => throw new InvalidOperationException(error);
        using var http = new HttpClient(new ReleaseUpdateCheckTests.Handler());
        var result = await HealthTools.Probe(false, default, new ReleaseUpdateCheck(http), Identity());
        var health = Data(result);
        Assert.True(result.IsError);
        Assert.True(health.Value<bool>("host_connected"));
        Assert.False(health.Value<bool>("health_supported"));
        Assert.Equal("HEALTH_UNSUPPORTED", health["error"].Value<string>("code"));
        Assert.Null(health["mcp"].Value<string>("plugin_version"));
        Assert.False(health["update_guidance"].Value<bool>("requires_user_authorization"));
    }

    [Fact]
    public async Task Explicit_update_check_preserves_transport_failure_and_missing_plugin_identity()
    {
        ToolGateway.SendOverride = (_, _, _) => throw new System.IO.IOException("not connected");
        var handler = new ReleaseUpdateCheckTests.Handler();
        using var http = new HttpClient(handler);
        var result = await HealthTools.Probe(true, default, new ReleaseUpdateCheck(http), Identity());
        var health = Data(result);
        Assert.True(result.IsError);
        Assert.False(health.Value<bool>("host_connected"));
        Assert.Equal("TARGET_UNAVAILABLE", health["error"].Value<string>("code"));
        Assert.Equal("1.1.0", health["mcp"].Value<string>("server_version"));
        Assert.Null(health["mcp"].Value<string>("plugin_version"));
        Assert.Equal(2, handler.Calls);
        Assert.Equal("checked", health["update"].Value<string>("status"));
        Assert.Equal("review", health["update_guidance"].Value<string>("mode"));
        Assert.False(health["update"]["notification"].Value<bool>("bundle_update_proposal_allowed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_payload_never_claims_a_successful_host_probe(bool isNull)
    {
        ToolGateway.SendOverride = (_, _, _) => Task.FromResult(isNull ? null : new JObject { ["ok"] = "true" });
        using var http = new HttpClient(new ReleaseUpdateCheckTests.Handler());
        var result = await HealthTools.Probe(false, default, new ReleaseUpdateCheck(http), Identity());
        var health = Data(result);
        Assert.True(result.IsError);
        Assert.False(health.Value<bool>("host_connected"));
        Assert.Equal("INVALID_HEALTH_RESPONSE", health["error"].Value<string>("code"));
        Assert.NotNull(health["setup_gateway"]);
    }

    [Fact]
    public async Task Cancellation_does_not_turn_into_health_or_update_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ToolGateway.SendOverride = (_, _, _) => throw new Xunit.Sdk.XunitException("Must not dispatch");
        using var http = new HttpClient(new ReleaseUpdateCheckTests.Handler());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HealthTools.Probe(true, cancellation.Token, new ReleaseUpdateCheck(http), Identity()));
    }

    public void Dispose() => ToolGateway.SendOverride = _originalSend;
}
