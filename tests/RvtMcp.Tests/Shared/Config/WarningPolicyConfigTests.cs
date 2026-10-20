using System;
using System.IO;
using Newtonsoft.Json;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests;

public class WarningPolicyConfigTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvt-warning-policy-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_folder, "config.json");

    [Theory]
    [InlineData("{}", "rollback")]
    [InlineData("{\"warningPolicy\":\"rollback\"}", "rollback")]
    [InlineData("{\"warningPolicy\":\"accept-and-log\"}", "accept-and-log")]
    [InlineData("{\"warningPolicy\":\" ACCEPT-AND-LOG \"}", "accept-and-log")]
    [InlineData("{\"warningPolicy\":\"accept\"}", "rollback")]
    [InlineData("{\"warningPolicy\":null}", "rollback")]
    [InlineData("{\"warningPolicy\":true}", "rollback")]
    [InlineData("{\"warningPolicy\":{}}", "rollback")]
    [InlineData("{\"warningPolicy\":[\"accept-and-log\"]}", "rollback")]
    public void Policy_is_strict_unless_explicitly_valid_and_does_not_discard_other_settings(string json, string expected)
    {
        var config = JsonConvert.DeserializeObject<RvtMcpConfig>(json);
        Assert.Equal(expected, config.WarningPolicyOrDefault);
        Directory.CreateDirectory(_folder);
        var root = Newtonsoft.Json.Linq.JObject.Parse(json);
        root["readOnly"] = true;
        File.WriteAllText(ConfigPath, root.ToString());
        Assert.True(RvtMcpConfig.LoadReadOnly(ConfigPath, _ => null).ReadOnlyOrDefault);
    }

    [Fact]
    public void Cli_overrides_environment_and_environment_overrides_json_without_writing_back()
    {
        Directory.CreateDirectory(_folder);
        const string json = "{\"warningPolicy\":\"rollback\",\"readOnly\":true}";
        File.WriteAllText(ConfigPath, json);
        string Env(string name) => name == "BIMWRIGHT_WARNING_POLICY" ? "accept-and-log" : null;
        Assert.Equal("accept-and-log", RvtMcpConfig.Load(null, ConfigPath, Env).WarningPolicyOrDefault);
        Assert.Equal("rollback", RvtMcpConfig.Load(new[] { "--warning-policy", "rollback" }, ConfigPath, Env).WarningPolicyOrDefault);
        Assert.Equal(json, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Only_explicit_session_overrides_travel_so_a_server_json_snapshot_cannot_undo_later_settings()
    {
        var server = JsonConvert.DeserializeObject<RvtMcpConfig>("{\"warningPolicy\":\"accept-and-log\"}");
        Assert.Equal("rollback", new RvtMcpConfig().WithRuntimeOptions(server.ToRuntimeOptions()).WarningPolicyOrDefault);
        RvtMcpConfig.ApplyCliArgs(server, new[] { "--warning-policy", "accept-and-log" });
        Assert.Equal("accept-and-log", new RvtMcpConfig().WithRuntimeOptions(server.ToRuntimeOptions()).WarningPolicyOrDefault);
        RvtMcpConfig.ApplyCliArgs(server, new[] { "--warning-policy", "rollback" });
        var host = JsonConvert.DeserializeObject<RvtMcpConfig>("{\"warningPolicy\":\"accept-and-log\",\"readOnly\":true}");
        var merged = host.WithRuntimeOptions(server.ToRuntimeOptions());
        Assert.Equal("rollback", merged.WarningPolicyOrDefault);
        Assert.True(merged.ReadOnlyOrDefault);
    }

    [Fact]
    public void Invalid_cli_value_is_strict_and_a_missing_value_does_not_swallow_the_next_flag()
    {
        var config = JsonConvert.DeserializeObject<RvtMcpConfig>("{\"warningPolicy\":\"accept-and-log\"}");
        RvtMcpConfig.ApplyCliArgs(config, new[] { "--warning-policy", "--read-only" });
        Assert.Equal("rollback", config.WarningPolicyOrDefault);
        Assert.True(config.ReadOnlyOrDefault);
        RvtMcpConfig.ApplyEnvVars(config, name => name == "BIMWRIGHT_WARNING_POLICY" ? "bad" : null, null, false);
        Assert.Equal("rollback", config.WarningPolicyOrDefault);
    }

    [Fact]
    public void Settings_patch_persists_the_mode_and_preserves_unrelated_keys()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(ConfigPath, "{\"readOnly\":true,\"custom\":{\"keep\":1}}");
        var result = RvtMcpConfig.TryApplySettings(new RvtMcpConfig.SettingsPatch { WarningPolicy = "accept-and-log" }, ConfigPath);
        Assert.Empty(result.Failed);
        Assert.Contains("warningPolicy", result.Applied);
        var reread = RvtMcpConfig.LoadReadOnly(ConfigPath, _ => null);
        Assert.Equal("accept-and-log", reread.WarningPolicyOrDefault);
        Assert.True(reread.ReadOnlyOrDefault);
        Assert.Equal(1, (int)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ConfigPath))["custom"]!["keep"]!);
        var previous = File.ReadAllText(ConfigPath);
        Assert.False(RvtMcpConfig.TrySaveWarningPolicy("bad", out _, ConfigPath));
        Assert.Equal(previous, File.ReadAllText(ConfigPath));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
