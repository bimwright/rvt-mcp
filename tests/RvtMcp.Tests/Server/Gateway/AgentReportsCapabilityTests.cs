using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;
using ServerAuthToken = RvtMcp.Server.AuthToken;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class AgentReportsCapabilityTests
    {
        [Fact]
        public async Task Missing_capability_is_rejected_before_the_report_reaches_the_addin()
        {
            using var wire = new ReportTestWire(agentReports: false);
            var result = await wire.Send();

            var error = result.Value<string>("error");
            Assert.StartsWith("AGENT_REPORTS_CAPABILITY_REQUIRED:", error);
            Assert.Contains("gateway", error);
            Assert.Contains("add-in", error);
            Assert.Contains("Setup", error);
            Assert.Equal("not_started", result["_operation"].Value<string>("outcome"));
            Assert.Empty(wire.Requests);
        }

        [Fact]
        public async Task Advertised_capability_allows_the_report_to_reach_the_addin()
        {
            using var wire = new ReportTestWire(agentReports: true);
            var result = await wire.Send();

            Assert.True(result.Value<bool>("ok"));
            Assert.Equal("report_task_result", Assert.Single(wire.Requests).Value<string>("command"));
        }

        [Fact]
        public async Task Fake_host_diagnostics_are_written_only_to_the_test_directory()
        {
            using var wire = new ReportTestWire(agentReports: true);
            await wire.Send();
            Assert.Equal(TestDiagnostics.Root, DiagnosticLog.LocalAppDataOverride);
            Assert.True(DiagnosticLog.Flush(5000));
            var log = Path.Combine(TestDiagnostics.Root, "Bimwright", "rvt-mcp", "rvt-mcp.log");
            Assert.Contains("revit-2024-484848", File.ReadAllText(log));
        }
    }

    // An isolated fake descriptor and loopback add-in; never scans or contacts a live Revit.
    internal sealed class ReportTestWire : IDisposable
    {
        private const int Pid = 484848;
        private static readonly DateTime Start = new DateTime(2026, 10, 8, 8, 0, 0, DateTimeKind.Utc);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-report-wire-" + Guid.NewGuid().ToString("N"));
        private readonly string _oldDir = ServerAuthToken.DiscoveryDirOverride;
        private readonly RvtMcpConfig _oldConfig = ServerState.Config;
        private readonly RvtMcp.ToolCatalog.ToolCatalog _oldCatalog = ServerState.ToolCatalog;
        private readonly Func<string, object, int?, Task<JObject>> _oldSend = ToolGateway.SendOverride;
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private TcpClient _client;
        public ConcurrentQueue<JObject> Requests { get; } = new ConcurrentQueue<JObject>();

        public ReportTestWire(bool agentReports, bool readOnly = false, bool confirmDocument = true)
        {
            Directory.CreateDirectory(_root);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(Serve) { IsBackground = true }.Start();
            var capabilities = new JArray("tool_catalog", "controlled_failures_v1", "document_target_v1");
            if (agentReports) capabilities.Add("agent_reports_v1");
            var descriptor = new JObject
            {
                ["schema_version"] = 3, ["host_app"] = "revit", ["host_year"] = 2024,
                ["pid"] = Pid, ["process_start_utc"] = Start.ToString("o"), ["target_id"] = "revit-2024-" + Pid,
                ["transport"] = "tcp", ["port"] = port, ["auth_token"] = "synthetic-token",
                ["capabilities"] = capabilities
            };
            File.WriteAllText(Path.Combine(_root, "revit-2024-" + Pid + ".json"), descriptor.ToString(Formatting.None));
            ServerAuthToken.DiscoveryDirOverride = _root;
            ToolGateway.ResetForTests();
            ToolGateway.SendOverride = null;
            var config = new RvtMcpConfig { EnableChangeHistory = false, ReadOnly = readOnly };
            ServerState.Config = config;
            ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
            RevitTargetBinding.ResetForTests(new FakeProbe());
            RevitTargetBinding.Switch(TargetSelector.ForPid(Pid), RevitTargetBinding.Scan().Live);
            if (confirmDocument) RevitTargetBinding.Binding.ConfirmDocument(RevitTargetBinding.CaptureGeneration(), "synthetic-doc");
        }

        private sealed class FakeProbe : IProcessProbe
        {
            public ProcessProbeResult Probe(int pid) => pid == Pid
                ? new ProcessProbeResult(ProcessState.Running, Start, "Synthetic Revit")
                : new ProcessProbeResult(ProcessState.NotFound, null, null);
        }

        private void Serve()
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                _client = client;
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var request = JObject.Parse(line);
                    if (request.Value<string>("command") != "set_tool_catalog") Requests.Enqueue(request);
                    var response = new JObject { ["id"] = request["id"], ["success"] = true, ["data"] = new JObject { ["ok"] = true } };
                    writer.WriteLine(response.ToString(Formatting.None));
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        public async Task<JObject> Send()
        {
            var context = TargetCallContext.Begin();
            try { return await ToolGateway.SendToRevit("report_task_result", new { task_id = "job-1", outcome = "completed", summary = "Done" }); }
            finally { TargetCallContext.End(context); }
        }

        public void Dispose()
        {
            ToolGateway.ResetForTests();
            RevitTargetBinding.ResetForTests();
            ServerAuthToken.DiscoveryDirOverride = _oldDir;
            ServerState.Config = _oldConfig;
            ServerState.ToolCatalog = _oldCatalog;
            ToolGateway.SendOverride = _oldSend;
            _client?.Close();
            _listener.Stop();
            try { TestDiagnostics.DeleteTempDirectory(_root, "rvt-report-wire-"); } catch (IOException) { }
        }
    }
}
