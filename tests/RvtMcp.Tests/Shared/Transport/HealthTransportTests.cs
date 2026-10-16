using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class HealthTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticated_health_replies_while_modal_without_queueing_or_confirming_a_document(bool usePipe)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rvt-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pipeName = "rvt-health-" + Guid.NewGuid().ToString("N");
        using ITransportServer server = usePipe ? new PipeTransportServer(directory, _ => { }, pipeName)
            : new TcpTransportServer(directory, _ => { });
        HostHealthSnapshot.Initialize(typeof(HealthTransportTests).Assembly, 2024);
        HostHealthSnapshot.ObserveDocument(false, null, null);
        var callbacks = 0;
        try
        {
            server.Start((_, tcs) => { callbacks++; tcs.TrySetResult("{}"); });
            PendingRequestLifecycle.SetHostBusy(true);
            using var connection = await Connect(server, usePipe, pipeName);
            using var reader = new StreamReader(connection, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(connection, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(new JObject
            { ["id"] = "probe", ["command"] = "health", ["token"] = AuthToken.Current }.ToString(Formatting.None));
            var reply = JObject.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("probe", reply.Value<string>("id"));
            Assert.True(reply.Value<bool>("success"));
            var data = (JObject)reply["data"];
            Assert.True(data.Value<bool>("waiting_for_user"));
            Assert.False(data.Value<bool>("ready_for_commands"));
            Assert.False(data.Value<bool>("has_active_document"));
            Assert.Equal("last_ui_observation", data.Value<string>("state_probe"));
            Assert.True(DateTimeOffset.TryParse(data.Value<string>("document_observed_utc"), out _));
            Assert.Equal("loaded_assembly", data["setup_plugin"].Value<string>("identity_basis"));
            Assert.Null(data["_target"]);
            Assert.Null(reply["_operation"]);
            Assert.Equal(0, callbacks);
        }
        finally
        {
            PendingRequestLifecycle.SetHostBusy(false);
            server.Stop();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Health_never_bypasses_transport_token_authentication(bool usePipe)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rvt-health-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pipeName = "rvt-health-auth-" + Guid.NewGuid().ToString("N");
        using ITransportServer server = usePipe ? new PipeTransportServer(directory, _ => { }, pipeName)
            : new TcpTransportServer(directory, _ => { });
        try
        {
            server.Start((_, _) => throw new Xunit.Sdk.XunitException("Must not reach the UI queue"));
            using var connection = await Connect(server, usePipe, pipeName);
            using var reader = new StreamReader(connection, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(connection, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(new JObject
            { ["id"] = "rejected", ["command"] = "health", ["token"] = "wrong-token" }.ToString(Formatting.None));
            var reply = JObject.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(reply.Value<bool>("success"));
            Assert.StartsWith("Unauthorized:", reply.Value<string>("error"));
            Assert.Null(reply["data"]);
        }
        finally
        {
            server.Stop();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void An_unobserved_document_is_unknown_and_snapshot_callers_cannot_mutate_later_replies()
    {
        HostHealthSnapshot.Initialize(typeof(HealthTransportTests).Assembly, 2027);
        var unknown = HostHealthSnapshot.Capture(0, false);
        Assert.Null(unknown.Value<bool?>("has_active_document"));
        Assert.Null(unknown.Value<bool?>("ready_for_commands"));
        Assert.Equal("not_observed", unknown.Value<string>("state_probe"));
        HostHealthSnapshot.ObserveDocument(true, "Sample", false);
        var first = HostHealthSnapshot.Capture(2, false);
        first["setup_plugin"]["host_year"] = 1900;
        first["document_title"] = "changed";
        var next = HostHealthSnapshot.Capture(2, false);
        Assert.Equal(2027, next.Value<int>("revit_year"));
        Assert.Equal(2027, next["setup_plugin"].Value<int>("host_year"));
        Assert.Equal("Sample", next.Value<string>("document_title"));
        Assert.Equal("commands_in_flight", next.Value<string>("host_state"));
        Assert.Null(next.Value<bool?>("ready_for_commands"));
        HostHealthSnapshot.ForgetDocument();
        Assert.Null(HostHealthSnapshot.Capture(0, false).Value<bool?>("has_active_document"));
    }

    private static async Task<Stream> Connect(ITransportServer server, bool usePipe, string name)
    {
        if (usePipe)
        {
            var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(3000);
            return stream;
        }
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", ((TcpTransportServer)server).Port);
        return client.GetStream();
    }
}
