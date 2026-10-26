using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class ScriptIntegrationTests
    {
        [Theory]
        [InlineData("public class Collision {}", "public class Collision {}", "CS0101")]
        [InlineData("public class Collision { public void Run() {} public void Run() {} }", "class Other {}", "CS0111")]
        [InlineData("public class Collision { public void Run(int n) {} }", "class Other { void Use() { new Collision().Run(); } }", "CS7036")]
        public void Diagnostic_collisions_and_overload_errors_retain_the_original_module_source(string first, string second, string diagnostic)
        {
            var modules = new[] {new ScriptModuleRevision("first", first), new ScriptModuleRevision("second", second, new[] {"first"})};
            var result = SendCodeCompilerTests.Compile(new ScriptInput("return null;", modules: new[] {"second"}, compileOnly: true), modules);
            Assert.False(result.Success); Assert.Equal(ScriptErrorCodes.CompileFailed, result.ErrorCode);
            Assert.Contains(result.Diagnostics, d => d.DiagnosticId == diagnostic && d.Source.StartsWith("module:", StringComparison.Ordinal) && d.Line > 0);
            Assert.Null(result.AssemblyBytes);
        }

        [Fact]
        public async Task Oversized_module_source_listing_spills_without_losing_source_or_journaling_it()
        {
            using var fixture = new CodeModuleStoreTests();
            var source = "public class BulkHelper { /*PRIVATE-MODULE-SOURCE-" + new string('z', 20000) + "*/ }";
            await fixture.Save("bulk", source);
            var previous = CodeModuleTools.StoreFactory;
            CodeModuleTools.StoreFactory = () => new RvtMcp.Server.Bake.BakeDb(fixture.DbPath);
            try
            {
                var text = CodeModuleReadTools.ListCodeModules(include_code: true);
                var root = Path.GetDirectoryName(fixture.DbPath);
                var session = new SessionContext(true, Path.Combine(root, "journal"));
                var config = new RvtMcpConfig {EnableResponseGuard = true, ResponseWarnBytes = 2048,
                    ResponseStrongWarnBytes = 3072, ResponseBudgetBytes = 4096, MaxResponseBytes = 8192};
                var result = RuntimeToolFilter.Apply(new CallToolRequestParams {Name = "revit_list_code_modules"},
                    new CallToolResult {Content = new[] {new TextContentBlock {Text = text}}}, config, session,
                    writer: new ResponseSpillWriter(Path.Combine(root, "spill")));
                Assert.False(result.IsError == true);
                var reply = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
                Assert.Equal("file", reply.Value<string>("output_mode"));
                Assert.False(reply.Value<bool>("mutation_applied"));
                var path = reply.Value<string>("path"); Assert.True(File.Exists(path));
                Assert.StartsWith(Path.Combine(root, "spill"), path);
                var spilled = JObject.Parse(File.ReadAllText(path));
                Assert.Equal(source, spilled["modules"][0].Value<string>("code"));
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= 7680);
                var audit = Assert.Single(session.Journal.ReadDay(DateTime.UtcNow.ToString("yyyy-MM-dd")));
                Assert.DoesNotContain("PRIVATE-MODULE-SOURCE", audit.Result);
            }
            finally { CodeModuleTools.StoreFactory = previous; }
        }
    }
}
