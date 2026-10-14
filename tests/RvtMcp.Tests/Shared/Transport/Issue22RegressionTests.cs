using System;
using System.IO;
using System.IO.Pipes;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class Issue22RegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvtmcp-i22-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Busy_pipe_start_fails_without_publishing_or_claiming_to_run()
    {
        var name = "rvtmcp-i22-" + Guid.NewGuid().ToString("N");
        using var owner = new NamedPipeServerStream(name, PipeDirection.InOut, 1);
        using var server = new PipeTransportServer(_root, _ => { }, name);
        Assert.Throws<IOException>(() => server.Start((_, tcs) => tcs.TrySetResult("{}")));
        Assert.False(server.IsRunning);
        Assert.False(Directory.Exists(_root) && Directory.GetFiles(_root, "revit-*.json").Length > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_stopped_transport_rejects_reuse_before_opening_a_new_endpoint(bool pipe)
    {
        ITransportServer server = pipe
            ? new PipeTransportServer(_root, _ => { }, "rvtmcp-i22-" + Guid.NewGuid().ToString("N"))
            : new TcpTransportServer(_root, _ => { });
        try
        {
            server.Start((_, tcs) => tcs.TrySetResult("{}"));
            server.Stop();
            Assert.Throws<InvalidOperationException>(() => server.Start((_, tcs) => tcs.TrySetResult("{}")));
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public void Call_log_rotates_during_a_long_session_before_the_next_record_exceeds_the_cap()
    {
        var previous = McpLogger.LocalAppDataOverride;
        try
        {
            McpLogger.LocalAppDataOverride = _root;
            McpLogger.Initialize(true);
            var path = McpLogger.CurrentLogPath;
            for (var i = 0; i < 330; i++)
                McpLogger.Log("probe", "{\"payload\":\"" + new string('a', 7600) + "\"}", true, 1,
                    resultJson: "{\"message\":\"" + new string('b', 9500) + "\"}");
            Assert.True(new FileInfo(path).Length <= 5 * 1024 * 1024);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "mcp-calls-*.jsonl"));
        }
        finally
        {
            McpLogger.Initialize(false);
            McpLogger.LocalAppDataOverride = previous;
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
