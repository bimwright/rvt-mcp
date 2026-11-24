using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using Xunit;

namespace RvtMcp.Tests
{
    // Issue #68: another add-in (pyRevit) can load a different Roslyn into the default load context first.
    public sealed class RoslynHostTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtmcp-roslyn-" + Guid.NewGuid().ToString("N"));
        private readonly string[] _references;

        public RoslynHostTests()
        {
            Directory.CreateDirectory(_dir);
            _references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(new[] { typeof(JObject).Assembly.Location, typeof(ScriptInput).Assembly.Location, FakeRevitApi() })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string FakeRevitApi()
        {
            var name = "ScriptApi_" + Guid.NewGuid().ToString("N");
            var source = @"namespace Autodesk.Revit.DB { public class Document {} }
namespace Autodesk.Revit.DB.Architecture { public class Stairs {} }
namespace Autodesk.Revit.DB.Electrical { public class CableTray {} }
namespace Autodesk.Revit.DB.Mechanical { public class Duct {} }
namespace Autodesk.Revit.DB.Plumbing { public class Pipe {} }
namespace Autodesk.Revit.DB.Structure { public enum StructuralType { NonStructural } }
namespace Autodesk.Revit.ApplicationServices { public class Application { public string VersionNumber => ""2027""; } }
namespace Autodesk.Revit.UI {
 public class UIApplication { public Autodesk.Revit.ApplicationServices.Application Application => null; public UIDocument ActiveUIDocument => null; }
 public class UIDocument { public Autodesk.Revit.DB.Document Document => null; }
}";
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var path = Path.Combine(_dir, name + ".dll");
            var emitted = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, platform,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(path);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            return path;
        }

        [Fact]
        public void Isolated_context_compiles_with_its_own_copy_of_Roslyn_not_the_default_contexts()
        {
            var self = typeof(RoslynHost).Assembly;
            var context = new RoslynLoadContext(Path.GetDirectoryName(self.Location)!, self.GetName().Name!);
            var bridge = context.LoadFromAssemblyName(self.GetName()).GetType(typeof(RoslynBridge).FullName!, true)!;

            var json = (string)bridge.GetMethod(nameof(RoslynBridge.CompileLegacy))!
                .Invoke(null, new object[] { "return app.Application.VersionNumber;", _references })!;

            var output = JObject.Parse(json);
            Assert.True(output.Value<bool>("Success"), json);
            Assert.NotEmpty(Convert.FromBase64String(output.Value<string>("AssemblyBytes")!));
            Assert.NotSame(self, bridge.Assembly);
            var isolatedCompiler = context.Assemblies.Single(a => a.GetName().Name == "Microsoft.CodeAnalysis");
            Assert.NotSame(typeof(Compilation).Assembly, isolatedCompiler);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(Compilation).Assembly));
            // Shared dependencies still come from the default context, so JSON and host types stay single.
            Assert.DoesNotContain(context.Assemblies, a => a.GetName().Name == typeof(JObject).Assembly.GetName().Name);
        }

        [Fact]
        public void Script_compile_through_the_host_returns_bytes_and_diagnostics_as_host_types()
        {
            var ok = RoslynHost.CompileScript(new ScriptInput("return app.Application.VersionNumber;"), Array.Empty<ScriptModuleRevision>(),
                _references, null, out var skipped);
            Assert.True(ok.Success, ok.ErrorCode + ": " + string.Join("\n", ok.Diagnostics.Select(d => d.Message)));
            Assert.NotEmpty(ok.AssemblyBytes);
            Assert.Empty(skipped);

            var failed = RoslynHost.CompileScript(new ScriptInput("return missingName;", compileOnly: true), Array.Empty<ScriptModuleRevision>(),
                _references, null, out _);
            Assert.False(failed.Success);
            Assert.Equal(ScriptErrorCodes.CompileFailed, failed.ErrorCode);
            var diagnostic = Assert.Single(failed.Diagnostics, d => d.DiagnosticId == "CS0103");
            Assert.Equal("script", diagnostic.Source);
            Assert.Equal(1, diagnostic.Line);
        }

        [Fact]
        public void Script_compile_starting_callback_runs_across_the_context_boundary()
        {
            var calls = 0;
            RoslynHost.CompileScript(new ScriptInput("return 1;", compileOnly: true), Array.Empty<ScriptModuleRevision>(),
                _references, () => calls++, out _);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Contract_error_inside_the_isolated_compiler_is_rethrown_as_the_host_exception_type()
        {
            var input = new ScriptInput("return helper.Value();", modules: new[] { "helper" });
            var ex = Assert.Throws<ScriptContractException>(() =>
                RoslynHost.CompileScript(input, Array.Empty<ScriptModuleRevision>(), _references, null, out _));
            Assert.Equal(ScriptErrorCodes.ModuleNotFound, ex.ErrorCode);
        }

        [Fact]
        public void Baked_tool_compile_through_the_host_returns_assembly_bytes()
        {
            var output = RoslynHost.CompileTool("public class BakedTool_ok { public object Run() { return 42; } }", _references);
            Assert.True(output.Success, output.Error);
            Assert.NotEmpty(output.AssemblyBytes);
        }

        [Fact]
        public void Baked_tool_policy_denial_from_the_isolated_compiler_reaches_the_host_unchanged()
        {
            var output = RoslynHost.CompileTool(
                "public class BakedTool_io { public object Run() { System.IO.File.Delete(\"x\"); return null; } }", _references);
            Assert.False(output.Success);
            Assert.StartsWith("SecurityViolation:", output.Error);
            Assert.Equal(new[] { output.Error }, output.Diagnostics);
            Assert.Null(output.AssemblyBytes);
        }

        [Fact]
        public void Failure_to_load_the_compiler_is_reported_as_SCRIPT_COMPILER_UNAVAILABLE_not_input_invalid()
        {
            var load = new FileLoadException("The located assembly's manifest definition does not match the assembly reference. (0x80131040)",
                "Microsoft.CodeAnalysis, Version=4.13.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
            var unavailable = RoslynHost.CompilerUnavailable(new TargetInvocationException(load));
            Assert.NotNull(unavailable);
            Assert.Equal(ScriptErrorCodes.CompilerUnavailable, unavailable!.ErrorCode);
            Assert.Contains("not a script error", unavailable.Message);
            Assert.Contains("0x80131040", unavailable.Message);
        }

        [Fact]
        public void Other_load_failures_are_not_classified_as_compiler_unavailable()
        {
            Assert.Null(RoslynHost.CompilerUnavailable(new FileLoadException("x", "Newtonsoft.Json, Version=13.0.0.0")));
            Assert.Null(RoslynHost.CompilerUnavailable(new InvalidOperationException("x")));
        }

        [Fact]
        public void Unavailable_message_names_the_other_add_ins_compiler_and_skips_its_own()
        {
            var own = Path.Combine(_dir, "RvtMcp");
            var other = Path.Combine(_dir, "OtherAddin", "bin", "netcore");
            var message = RoslynHost.DescribeUnavailable("detail", new[]
            {
                Tuple.Create(new Version(4, 13, 0, 0), Path.Combine(own, "Microsoft.CodeAnalysis.dll")),
                Tuple.Create(new Version(4, 11, 0, 0), Path.Combine(other, "Microsoft.CodeAnalysis.dll"))
            }, own);
            Assert.Contains("Microsoft.CodeAnalysis 4.11.0.0 from OtherAddin/bin/netcore.", message);
            Assert.DoesNotContain("4.13.0.0", message);
            Assert.EndsWith("Detail: detail", message);
        }

        [Fact]
        public void Unavailable_message_keeps_the_other_add_ins_folder_name_after_response_redaction()
        {
            var message = RoslynHost.DescribeUnavailable("detail", new[]
            {
                Tuple.Create(new Version(4, 11, 0, 0), @"C:\Users\someone\AppData\Roaming\OtherAddin-Master\bin\netcore\Microsoft.CodeAnalysis.dll")
            }, @"C:\Users\someone\AppData\Roaming\Autodesk\Revit\Addins\2026\RvtMcp");

            var redacted = McpResponsePrivacy.RedactErrorForResponse(ScriptErrorCodes.CompilerUnavailable + ": " + message);

            Assert.Contains("Microsoft.CodeAnalysis 4.11.0.0 from OtherAddin-Master/bin/netcore.", redacted);
            Assert.DoesNotContain("someone", redacted);
            Assert.DoesNotContain(@"C:\", redacted);
        }

        [Theory]
        [InlineData(@"C:\Users\someone\AppData\Roaming\OtherAddin\bin\netfx", "OtherAddin/bin/netfx")]
        [InlineData(@"C:\Users\someone\OtherAddin", "OtherAddin")]
        [InlineData(@"D:\Tools", "Tools")]
        [InlineData(@"\\server\share\addins\OtherAddin", "share/addins/OtherAddin")]
        public void Add_in_folder_keeps_the_last_three_folders_without_drive_or_user_name(string directory, string expected)
            => Assert.Equal(expected, RoslynHost.AddinFolder(directory));
    }
}
