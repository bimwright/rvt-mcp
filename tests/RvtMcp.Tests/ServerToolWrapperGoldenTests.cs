using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Pins what every forwarding tool wrapper does on the wire: the command, the exact parameter JSON,
    /// the timeout, and the text handed back to the agent when the plug-in answers or the call throws.
    /// Refactors of the server tool shells must leave Golden/server-tool-wrappers.json unchanged.
    /// Set RVTMCP_UPDATE_GOLDEN=1 to rewrite the file after an intended change.
    /// </summary>
    [Collection("ServerStateConfig")]
    public sealed class ServerToolWrapperGoldenTests
    {
        [Fact]
        public async Task Tool_wrappers_match_the_recorded_wire_calls_and_results()
        {
            var actual = new JObject();
            foreach (var (name, method) in EveryToolContractTests.AllTools()
                .Where(t => !EveryToolContractTests.ServerOnly.ContainsKey(t.Name))
                .OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                actual[name] = new JObject
                {
                    ["success"] = await Record(method, fail: false),
                    ["failure"] = await Record(method, fail: true),
                };
            }

            var path = Path.Combine(RepoRoot(), "tests", "RvtMcp.Tests", "Golden", "server-tool-wrappers.json");
            var text = actual.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
            if (Environment.GetEnvironmentVariable("RVTMCP_UPDATE_GOLDEN") == "1")
                File.WriteAllText(path, text);

            Assert.True(File.Exists(path), "Missing golden file " + path);
            var expected = JObject.Parse(File.ReadAllText(path));
            actual = JObject.Parse(text); // compare parsed tokens on both sides (null token types differ otherwise)
            foreach (var property in expected.Properties())
                Assert.True(JToken.DeepEquals(property.Value, actual[property.Name]),
                    property.Name + " changed:\n" + actual[property.Name]);
            Assert.Equal(expected.Properties().Select(p => p.Name), actual.Properties().Select(p => p.Name));
        }

        private static async Task<JObject> Record(System.Reflection.MethodInfo method, bool fail)
        {
            var calls = new JArray();
            var originalSend = ToolGateway.SendOverride;
            var originalConfig = ServerState.Config;
            ServerState.Config = new RvtMcpConfig();
            ToolGateway.SendOverride = (command, args, timeout) =>
            {
                calls.Add(new JObject
                {
                    ["command"] = command,
                    ["params"] = args == null ? null : JsonConvert.SerializeObject(args),
                    ["timeout"] = timeout,
                });
                if (fail) throw new InvalidOperationException("golden probe failure");
                return Task.FromResult(new JObject { ["golden_probe"] = true, ["items"] = new JArray(1, 2) });
            };
            try
            {
                string result;
                try { result = await EveryToolContractTests.Invoke(method); }
                catch (Exception ex) { result = "THREW " + ex.GetType().Name + ": " + ex.Message; }
                return new JObject { ["calls"] = calls, ["result"] = result };
            }
            finally
            {
                ToolGateway.SendOverride = originalSend;
                ServerState.Config = originalConfig;
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "server", "RvtMcp.Server.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
