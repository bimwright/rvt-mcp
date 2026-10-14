using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests;

public sealed class RvtMcpConfigTransportTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rvtmcp-transport-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Unconfigured_startup_remains_on_without_creating_a_file()
    {
        var config = RvtMcpConfig.LoadReadOnly(_path, _ => null);
        Assert.Null(config.AutoStartTransport);
        Assert.True(config.AutoStartTransportOrDefault);
        Assert.False(File.Exists(_path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Last_saved_toggle_is_used_on_the_next_load_and_preserves_other_settings(bool lastState)
    {
        const string until = "2030-01-01T01:02:03+07:00";
        File.WriteAllText(_path, "{\"readOnly\":true,\"target\":\"2024\",\"ownerExtension\":{\"keep\":true},\"persistSendCodeBodiesUntil\":\"" + until + "\"}");
        Assert.True(RvtMcpConfig.TrySaveAutoStartTransport(!lastState, out var error, _path), error);
        // Another setting saved between toggles must survive the final toggle too.
        Assert.True(RvtMcpConfig.TrySaveEnableToast(false, out error, _path), error);
        Assert.True(RvtMcpConfig.TrySaveAutoStartTransport(lastState, out error, _path), error);

        var config = RvtMcpConfig.LoadReadOnly(_path, _ => null);
        Assert.Equal(lastState, config.AutoStartTransportOrDefault);
        Assert.True(config.ReadOnlyOrDefault);
        Assert.False(config.EnableToastOrDefault);
        Assert.Equal("2024", config.Target);
        var root = JObject.Parse(File.ReadAllText(_path));
        Assert.True(root["ownerExtension"]!.Value<bool>("keep"));
        Assert.Equal(until, config.PersistSendCodeBodiesUntil);
    }

    [Fact]
    public void First_off_toggle_creates_the_preference_in_the_shared_config()
    {
        Assert.True(RvtMcpConfig.TrySaveAutoStartTransport(false, out var error, _path), error);
        Assert.False(RvtMcpConfig.LoadReadOnly(_path, _ => null).AutoStartTransportOrDefault);
    }

    [Theory]
    [InlineData("1", false, true)]
    [InlineData("0", true, false)]
    [InlineData("true", false, true)]
    [InlineData("false", true, false)]
    [InlineData("invalid", false, false)]
    [InlineData(null, false, false)]
    public void Revit_environment_overrides_saved_state_without_rewriting_it(string env, bool saved, bool expected)
    {
        Assert.True(RvtMcpConfig.TrySaveAutoStartTransport(saved, out var error, _path), error);
        var before = File.ReadAllBytes(_path);
        var config = RvtMcpConfig.LoadReadOnly(_path,
            name => name == RvtMcpConfig.EnvAutoStartTransport ? env : null);
        Assert.Equal(expected, config.AutoStartTransportOrDefault);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Saving_does_not_replace_an_invalid_config(string contents)
    {
        File.WriteAllText(_path, contents);
        Assert.False(RvtMcpConfig.TrySaveAutoStartTransport(false, out var error, _path));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(contents, File.ReadAllText(_path));
    }

    [Fact]
    public void Per_request_runtime_options_do_not_change_the_plugin_startup_preference()
    {
        var config = new RvtMcpConfig { AutoStartTransport = false };
        Assert.Null(config.ToRuntimeOptions().AutoStartTransport);
        Assert.False(config.WithRuntimeOptions(new RvtMcpConfig { AutoStartTransport = true }).AutoStartTransportOrDefault);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
