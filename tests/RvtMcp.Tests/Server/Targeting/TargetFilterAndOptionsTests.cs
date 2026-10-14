using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Bimwright.Targeting;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// §6.10 payload surfacing: the call filter must replace the tool result with
    /// the target envelope even when the tool swallowed the exception into
    /// "Error: …" text. Plus the --target selector option surface.
    /// </summary>
    [Collection("ServerStateConfig")]
    public class TargetFilterAndOptionsTests
    {
        [Fact]
        public async Task Tool_that_caught_target_exception_still_returns_payload_envelope()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-target-filter-" + Guid.NewGuid().ToString("N"));
            var originalConfig = ServerState.Config;
            var originalSend = ToolGateway.SendOverride;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pipe = "rvt-tfilter-" + Guid.NewGuid().ToString("N");
            using var input = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var output = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(input.WaitForConnectionAsync(deadline.Token), output.ConnectAsync(deadline.Token));
            try
            {
                var config = new RvtMcpConfig { Toolsets = new List<string> { "query" } };
                ServerState.Config = config;
                var payload = new Bimwright.Targeting.TargetPayload
                {
                    Code = Bimwright.Targeting.TargetCode.TargetChanged,
                    NextStep = "The call was not sent because the target binding changed after the call arrived.",
                    Message = "TARGET_CHANGED: The call was not sent because the target binding changed after the call arrived.",
                };
                ToolGateway.SendOverride = (command, args, timeout) => throw new TargetException(payload);

                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                var mcp = builder.Services.AddMcpServer().WithStreamServerTransport(input, input);
                mcp = RuntimeToolFilter.Register(mcp, config, new SessionContext(false, root));
                Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
                using var host = builder.Build();
                await host.StartAsync(deadline.Token);
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(output, output), cancellationToken: deadline.Token);

                var response = await client.CallToolAsync("revit_get_element_details",
                    new Dictionary<string, object> { ["elementIds"] = new long[] { 1 } }, cancellationToken: deadline.Token);

                Assert.True(response.IsError == true);
                var text = ((TextContentBlock)response.Content[0]).Text;
                var env = JObject.Parse(text); // must be the §6.10 envelope, not "Error: …" text
                Assert.False(env.Value<bool>("success"));
                Assert.StartsWith("TARGET_CHANGED:", env.Value<string>("error"));
                Assert.Equal("TARGET_CHANGED", env["target"].Value<string>("code"));
                Assert.False(env["target"].Value<bool>("sent"));

                await client.DisposeAsync();
                await host.StopAsync(deadline.Token);
            }
            finally
            {
                ServerState.Config = originalConfig;
                ToolGateway.SendOverride = originalSend;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData("auto", "Auto")]
        [InlineData("2024", "Year")]
        [InlineData("pid:4242", "Pid")]
        [InlineData("id:revit-2024-4242", "TargetId")]
        public void Target_option_accepts_all_selector_forms(string raw, string expectedKind)
        {
            Assert.True(Program.TryResolveTargetSelector(raw, out var selector, out var error));
            Assert.Null(error);
            Assert.Equal(expectedKind, selector.Kind.ToString());
        }

        [Theory]
        [InlineData("R24")]
        [InlineData("1999")]
        [InlineData("pid:notanumber")]
        [InlineData("id:bogus")]
        [InlineData("2024-4242")]
        public void Target_option_rejects_invalid_selectors(string raw)
        {
            Assert.False(Program.TryResolveTargetSelector(raw, out _, out var error));
            Assert.NotNull(error);
        }

        [Fact]
        public void Target_option_rejects_r_code_with_educational_text()
        {
            Assert.False(Program.TryResolveTargetSelector("R24", out _, out var error));
            Assert.Contains("R-codes", error);
        }

        [Fact]
        public void Config_precedence_still_populates_target_from_all_layers()
        {
            var fromCli = RvtMcpConfig.Load(new[] { "--target", "pid:4242" }, configFilePath: null,
                envLookup: _ => null);
            Assert.Equal("pid:4242", fromCli.Target);

            var fromEnv = RvtMcpConfig.Load(new string[0], configFilePath: null,
                envLookup: name => name == RvtMcpConfig.EnvTarget ? "2024" : null);
            Assert.Equal("2024", fromEnv.Target);

            // CLI beats env.
            var both = RvtMcpConfig.Load(new[] { "--target", "2026" }, configFilePath: null,
                envLookup: name => name == RvtMcpConfig.EnvTarget ? "2024" : null);
            Assert.Equal("2026", both.Target);
        }
    }
}
