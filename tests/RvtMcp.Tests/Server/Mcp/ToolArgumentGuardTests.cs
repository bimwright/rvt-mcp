using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    // BW-ARGS-001 vectors (V-numbers from the family plan) over the real stdio protocol path, with raw
    // JSON-RPC frames so absent, null and malformed `arguments` are sent exactly as written.
    [Collection("ServerStateConfig")]
    public class ToolArgumentGuardTests
    {
        private const string Marker = "BWARGS-MARKER-7f3a";

        [Fact]
        public async Task V1_valid_call_dispatches_unchanged_without_warning()
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_create_level", "{\"elevation\":900,\"name\":\"L1\"}");
            Assert.False(r.IsError);
            Assert.Null(r.Data["_ignored_arguments"]);
            var sent = Assert.Single(h.Dispatched);
            Assert.Equal("create_level", sent.Command);
            Assert.Equal(900d, sent.Args.Value<double>("elevation"));
            Assert.Equal("L1", sent.Args.Value<string>("name"));
        }

        [Theory]
        [InlineData("")]                      // V2: no arguments member
        [InlineData(",\"arguments\":null")]   // V3
        [InlineData(",\"arguments\":{}")]
        public async Task V2_V3_missing_required_is_rejected_before_dispatch(string argumentsMember)
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallRawAsync("{\"name\":\"revit_create_level\"" + argumentsMember + "}");
            Assert.True(r.IsError);
            Assert.StartsWith("INVALID_ARGUMENT: Parameter validation failed: required field 'elevation' is missing",
                r.Data.Value<string>("error"));
            Assert.False(r.Data.Value<bool>("success"));
            var invalid = (JObject)r.Data["invalid_arguments"];
            Assert.Equal(new[] { "elevation" }, invalid["missing"].Values<string>());
            Assert.Empty(invalid["received"]);
            Assert.Contains(invalid["expected"], e => e.Value<string>("name") == "elevation" && e.Value<bool>("required"));
            Assert.Contains(invalid["expected"], e => e.Value<string>("name") == "name" && !e.Value<bool>("required"));
            Assert.Empty(h.Dispatched);
        }

        [Fact]
        public async Task V4_pascal_case_keys_are_rejected_with_did_you_mean()
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_create_level", "{\"Elevation\":900,\"Name\":\"L1\"}");
            Assert.True(r.IsError);
            var unknown = r.Data["invalid_arguments"]["unknown"];
            Assert.Contains(unknown, u => u.Value<string>("key") == "Elevation" && u.Value<string>("did_you_mean") == "elevation");
            Assert.Contains(unknown, u => u.Value<string>("key") == "Name" && u.Value<string>("did_you_mean") == "name");
            Assert.Contains("Rename 'Elevation' to 'elevation'.", r.Data.Value<string>("suggestion"));
            Assert.Equal(new[] { "Elevation", "Name" }, r.Data["invalid_arguments"]["received"].Values<string>());
            Assert.Empty(h.Dispatched);
        }

        [Fact]
        public async Task V5_snake_case_key_suggests_camel_case_parameter()
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_get_mep_element_connectors", "{\"element_id\":12}");
            Assert.True(r.IsError);
            Assert.Equal("elementId", r.Data["invalid_arguments"]["unknown"][0].Value<string>("did_you_mean"));
            Assert.Empty(h.Dispatched);
        }

        [Fact]
        public async Task V6_parameters_nested_under_an_undeclared_key_get_a_nesting_hint()
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_create_level", "{\"arguments\":{\"elevation\":900}}");
            Assert.True(r.IsError);
            Assert.Equal("arguments", r.Data["invalid_arguments"]["unknown"][0].Value<string>("key"));
            Assert.Contains("nested under 'arguments'", r.Data.Value<string>("suggestion"));
            Assert.Empty(h.Dispatched);
        }

        [Fact]
        public async Task V8_extra_key_runs_and_reports_ignored_arguments()
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_create_level", "{\"elevation\":900,\"extra\":1}");
            Assert.False(r.IsError);
            var ignored = (JObject)r.Data["_ignored_arguments"];
            var entry = Assert.Single(ignored["unknown"]);
            Assert.Equal("extra", entry.Value<string>("key"));
            Assert.Null(entry["did_you_mean"]);
            Assert.Contains("were ignored", ignored.Value<string>("note"));
            Assert.Equal(900d, Assert.Single(h.Dispatched).Args.Value<double>("elevation"));
        }

        [Fact]
        public async Task Optional_only_tool_with_misnamed_keys_runs_and_names_the_intended_parameters()
        {
            // rvt-mcp#21 symptom shape: defaults are used, but the response now says which keys were ignored.
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_get_model_warnings_summary", "{\"MaxExamplesPerType\":1,\"IncludeExamples\":false}");
            Assert.False(r.IsError);
            var unknown = r.Data["_ignored_arguments"]["unknown"];
            Assert.Contains(unknown, u => u.Value<string>("did_you_mean") == "max_examples_per_type");
            Assert.Contains(unknown, u => u.Value<string>("did_you_mean") == "include_examples");
            Assert.Equal(5, Assert.Single(h.Dispatched).Args.Value<int>("max_examples_per_type"));
        }

        [Fact]
        public async Task V9_unconvertible_value_keeps_the_sdk_baseline()
        {
            // A4 is deferred: binder failures are not reinterpreted and still reach the SDK's generic wrapper.
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_create_level", "{\"elevation\":\"abc\"}");
            Assert.True(r.IsError);
            Assert.Equal("An error occurred invoking 'revit_create_level'.", r.Text);
            Assert.Empty(h.Dispatched);
        }

        [Fact]
        public async Task V10_numeric_string_accepted_by_the_sdk_is_unchanged()
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_create_level", "{\"elevation\":\"900\"}");
            Assert.False(r.IsError);
            Assert.Equal(900d, Assert.Single(h.Dispatched).Args.Value<double>("elevation"));
        }

        [Fact]
        public async Task V11_optional_only_tool_without_arguments_is_unchanged()
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallRawAsync("{\"name\":\"revit_get_model_warnings_summary\"}");
            Assert.False(r.IsError);
            Assert.Null(r.Data["_ignored_arguments"]);
            Assert.Single(h.Dispatched);
        }

        [Fact]
        public async Task V12_unknown_tool_keeps_the_json_rpc_error()
        {
            await using var h = await Harness.StartAsync();
            var response = await h.RequestAsync("{\"name\":\"revit_no_such_tool\",\"arguments\":{}}");
            Assert.Equal(-32602, response["error"].Value<int>("code"));
        }

        [Theory]
        [InlineData("{\"Code\":\"" + Marker + "\"}")]
        [InlineData("{\"CODE\":\"" + Marker + "\"}")]
        [InlineData("{\"payload\":{\"code\":\"" + Marker + "\"}}")]
        [InlineData("{\"code\":\"return null;\",\"note\":\"" + Marker + "\"}")]
        public async Task V13_code_values_under_wrong_keys_never_reach_the_response_or_journal(string arguments)
        {
            await using var h = await Harness.StartAsync();
            var r = await h.CallAsync("revit_send_code_to_revit", arguments);
            Assert.DoesNotContain(Marker, r.Raw);
            Assert.Single(h.JournalLines());
            Assert.DoesNotContain(Marker, h.JournalText());
            Assert.DoesNotContain(h.Dispatched, d => d.Args.ToString().Contains(Marker));
        }

        [Fact]
        public async Task V15_rejected_call_is_journaled_as_failed_with_key_only_payload()
        {
            await using var h = await Harness.StartAsync();
            await h.CallAsync("revit_create_level", "{\"Elevation\":987654.25}");
            var line = JObject.Parse(Assert.Single(h.JournalLines()));
            Assert.False(line.Value<bool>("Success"));
            Assert.Equal("create_level", line.Value<string>("Tool"));
            var logged = JObject.Parse(line.Value<string>("Params"));
            Assert.True(logged.Value<bool>("rejected"));
            Assert.Equal("missing_required", logged.Value<string>("reason"));
            Assert.Equal(new[] { "elevation" }, logged["missing"].Values<string>());
            Assert.Equal(new[] { "Elevation" }, logged["received_keys"].Values<string>());
            Assert.DoesNotContain("987654", h.JournalText());
        }

        [Fact]
        public async Task V15_rejected_send_code_keeps_the_key_only_journal_payload()
        {
            await using var h = await Harness.StartAsync();
            var result = await h.CallAsync("revit_send_code_to_revit", "{\"Code\":\"" + Marker + "\"}");
            Assert.True(result.IsError);
            Assert.Empty(h.Dispatched);
            var line = JObject.Parse(Assert.Single(h.JournalLines()));
            Assert.False(line.Value<bool>("Success"));
            Assert.Equal("send_code_to_revit", line.Value<string>("Tool"));
            var logged = JObject.Parse(line.Value<string>("Params"));
            Assert.True(logged.Value<bool>("rejected"));
            Assert.Equal("missing_required", logged.Value<string>("reason"));
            Assert.Equal(new[] { "code" }, logged["missing"].Values<string>());
            Assert.Equal(new[] { "Code" }, logged["unknown"].Values<string>());
            Assert.Equal(new[] { "Code" }, logged["received_keys"].Values<string>());
            Assert.Null(logged["code_hash"]);
            Assert.DoesNotContain(Marker, h.JournalText());
        }

        [Fact]
        public async Task V15_long_rejection_summary_stays_complete_valid_json()
        {
            await using var h = await Harness.StartAsync();
            var keys = Enumerable.Range(0, 40).Select(i => $"extra_{i:D2}_" + new string('k', 80)).ToArray();
            var arguments = new JObject(keys.Select(key => new JProperty(key, Marker)));
            var result = await h.CallAsync("revit_create_level", arguments.ToString(Newtonsoft.Json.Formatting.None));
            Assert.True(result.IsError);
            Assert.Empty(h.Dispatched);
            var line = JObject.Parse(Assert.Single(h.JournalLines()));
            var text = line.Value<string>("Params");
            Assert.True(text.Length > 1024);
            var logged = JObject.Parse(text);
            Assert.True(logged.Value<bool>("rejected"));
            Assert.Equal(new[] { "elevation" }, logged["missing"].Values<string>());
            var expected = keys.Take(ToolArgumentGuard.MaxKeys).Select(key => key.Substring(0, ToolArgumentGuard.MaxNameLength));
            Assert.Equal(expected, logged["received_keys"].Values<string>());
            Assert.Equal(expected, logged["unknown"].Values<string>());
            Assert.DoesNotContain(Marker, h.JournalText());
        }

        [Fact]
        public async Task Rejection_like_keys_on_valid_send_code_do_not_bypass_code_hashing()
        {
            await using var h = await Harness.StartAsync();
            var code = "return \"" + Marker + "\";";
            var arguments = new JObject
            {
                ["code"] = code, ["rejected"] = true, ["reason"] = "missing_required", ["received_keys"] = new JArray("Code")
            };
            var result = await h.CallAsync("revit_send_code_to_revit", arguments.ToString(Newtonsoft.Json.Formatting.None));
            Assert.False(result.IsError);
            Assert.Single(h.Dispatched);
            Assert.NotNull(result.Data["_ignored_arguments"]);
            var line = JObject.Parse(Assert.Single(h.JournalLines()));
            var logged = JObject.Parse(line.Value<string>("Params"));
            Assert.Equal(BakeRedactor.HashBody(code), logged.Value<string>("code_hash"));
            Assert.Equal(code.Length, logged.Value<int>("code_length"));
            Assert.Null(logged["rejected"]);
            Assert.DoesNotContain(Marker, h.JournalText());
        }

        [Fact]
        public async Task A8_reported_key_names_are_capped_in_count_and_length()
        {
            await using var h = await Harness.StartAsync();
            var keys = Enumerable.Range(0, 40).Select(i => "\"" + new string('k', 80) + i + "\":1");
            var r = await h.CallAsync("revit_create_level", "{" + string.Join(",", keys) + "}");
            var invalid = r.Data["invalid_arguments"];
            Assert.Equal(ToolArgumentGuard.MaxKeys, invalid["received"].Count());
            Assert.All(invalid["received"].Values<string>(), k => Assert.Equal(ToolArgumentGuard.MaxNameLength, k.Length));
        }

        [Fact]
        public async Task Http_transport_applies_the_same_guard_and_journal_payload()
        {
            // RunHttpSse wires the same RuntimeToolFilter.Register; this mirrors that setup on a loopback port.
            var root = Path.Combine(Path.GetTempPath(), "rvt-bw-args-http-" + Guid.NewGuid().ToString("N"));
            var original = ServerState.Config; var originalSend = ToolGateway.SendOverride;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var dispatched = new List<Dispatch>();
            try
            {
                var config = new RvtMcpConfig
                {
                    Toolsets = new List<string> { "create", "meta" }, EnableCallLog = true, EnableChangeHistory = false
                };
                ServerState.Config = config;
                ToolGateway.SendOverride = (command, args, timeout) =>
                {
                    lock (dispatched) dispatched.Add(new Dispatch(command, JObject.FromObject(args)));
                    return Task.FromResult(new JObject { ["success"] = true, ["data"] = new JObject { ["ok"] = true } });
                };
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                var mcp = builder.Services.AddMcpServer().WithHttpTransport();
                mcp = RuntimeToolFilter.Register(mcp, config, new SessionContext(true, root));
                Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                await using var app = builder.Build();
                app.MapMcp();
                await app.StartAsync(deadline.Token);
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
                await using var client = await McpClient.CreateAsync(
                    new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(address) }),
                    cancellationToken: deadline.Token);

                var missing = await client.CallToolAsync("revit_create_level",
                    new Dictionary<string, object>(), cancellationToken: deadline.Token);
                var pascal = await client.CallToolAsync("revit_create_level",
                    new Dictionary<string, object> { ["Elevation"] = 987654.25 }, cancellationToken: deadline.Token);
                var extra = await client.CallToolAsync("revit_create_level",
                    new Dictionary<string, object> { ["elevation"] = 900, ["extra"] = 1 }, cancellationToken: deadline.Token);

                Assert.True(missing.IsError);
                Assert.StartsWith("INVALID_ARGUMENT:", JObject.Parse(((TextContentBlock)missing.Content[0]).Text).Value<string>("error"));
                Assert.True(pascal.IsError);
                Assert.Equal("elevation", JObject.Parse(((TextContentBlock)pascal.Content[0]).Text)
                    ["invalid_arguments"]["unknown"][0].Value<string>("did_you_mean"));
                Assert.NotEqual(true, extra.IsError);
                Assert.Equal("extra", JObject.Parse(((TextContentBlock)extra.Content[0]).Text)
                    ["_ignored_arguments"]["unknown"][0].Value<string>("key"));
                Assert.Equal(900d, Assert.Single(dispatched).Args.Value<double>("elevation"));

                var journal = Directory.GetFiles(root, "*.jsonl").SelectMany(File.ReadAllLines).Select(JObject.Parse).ToList();
                Assert.Equal(3, journal.Count);
                Assert.All(journal.Take(2), line => Assert.True(JObject.Parse(line.Value<string>("Params")).Value<bool>("rejected")));
                Assert.DoesNotContain(journal, line => line.ToString().Contains("987654"));
                await client.DisposeAsync();
                await app.StopAsync(deadline.Token);
            }
            finally
            {
                ServerState.Config = original; ToolGateway.SendOverride = originalSend;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private sealed record Dispatch(string Command, JObject Args);

        private sealed record CallResult(string Raw, bool IsError, string Text, JObject Data);

        private sealed class Harness : IAsyncDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-bw-args-" + Guid.NewGuid().ToString("N"));
            private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
            private RvtMcpConfig _originalConfig;
            private Func<string, object, int?, Task<JObject>> _originalSend;
            private NamedPipeServerStream _input;
            private NamedPipeClientStream _output;
            private StreamReader _reader;
            private IHost _host;
            private int _nextId = 10;

            public List<Dispatch> Dispatched { get; } = new();

            public static async Task<Harness> StartAsync()
            {
                var h = new Harness();
                await h.InitAsync();
                return h;
            }

            private async Task InitAsync()
            {
                _originalConfig = ServerState.Config;
                _originalSend = ToolGateway.SendOverride;
                var config = new RvtMcpConfig
                {
                    Toolsets = new List<string> { "create", "meta", "lint", "mep" },
                    EnableCallLog = true,
                    EnableChangeHistory = false
                };
                ServerState.Config = config;
                ToolGateway.SendOverride = (command, args, timeout) =>
                {
                    var observed = args == null ? new JObject() : JObject.FromObject(args);
                    lock (Dispatched) Dispatched.Add(new Dispatch(command, observed));
                    return Task.FromResult(new JObject { ["success"] = true, ["data"] = new JObject { ["ok"] = true } });
                };
                var pipe = "rvt-bw-args-" + Guid.NewGuid().ToString("N");
                _input = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _output = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await Task.WhenAll(_input.WaitForConnectionAsync(_deadline.Token), _output.ConnectAsync(_deadline.Token));
                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                var mcp = builder.Services.AddMcpServer().WithStreamServerTransport(_input, _input);
                mcp = RuntimeToolFilter.Register(mcp, config, new SessionContext(true, _root));
                Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
                _host = builder.Build();
                await _host.StartAsync(_deadline.Token);
                _reader = new StreamReader(_output, new UTF8Encoding(false));
                await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\","
                    + "\"capabilities\":{},\"clientInfo\":{\"name\":\"bw-args-tests\",\"version\":\"1\"}}}");
                await ReceiveAsync(1);
                await SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            }

            public Task<CallResult> CallAsync(string tool, string arguments)
                => CallRawAsync("{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}");

            public async Task<CallResult> CallRawAsync(string paramsJson)
            {
                var response = await RequestAsync(paramsJson);
                var result = (JObject)response["result"];
                var text = result["content"][0].Value<string>("text");
                JObject data = null;
                try { data = JObject.Parse(text); } catch (Newtonsoft.Json.JsonReaderException) { }
                return new CallResult(response.ToString(), result.Value<bool?>("isError") == true, text, data);
            }

            public async Task<JObject> RequestAsync(string paramsJson)
            {
                var id = _nextId++;
                await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":" + paramsJson + "}");
                return await ReceiveAsync(id);
            }

            // SessionContext's journalDir holds the <date>.jsonl files directly.
            public string[] JournalLines()
                => Directory.Exists(_root)
                    ? Directory.GetFiles(_root, "*.jsonl").SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToArray()
                    : Array.Empty<string>();

            public string JournalText() => string.Join("\n", JournalLines());

            private async Task SendAsync(string line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _output.WriteAsync(bytes, 0, bytes.Length, _deadline.Token);
                await _output.FlushAsync(_deadline.Token);
            }

            private async Task<JObject> ReceiveAsync(int id)
            {
                while (true)
                {
                    var line = await _reader.ReadLineAsync().WaitAsync(_deadline.Token);
                    if (line == null) throw new EndOfStreamException("server closed the stream");
                    var message = JObject.Parse(line);
                    if (message["id"]?.Type == JTokenType.Integer && message.Value<int>("id") == id) return message;
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (_host != null) { await _host.StopAsync(TimeSpan.FromSeconds(5)); _host.Dispose(); }
                }
                finally
                {
                    _output?.Dispose();
                    _input?.Dispose();
                    _deadline.Dispose();
                    ServerState.Config = _originalConfig;
                    ToolGateway.SendOverride = _originalSend;
                    if (Directory.Exists(_root)) Directory.Delete(_root, true);
                }
            }
        }
    }
}
