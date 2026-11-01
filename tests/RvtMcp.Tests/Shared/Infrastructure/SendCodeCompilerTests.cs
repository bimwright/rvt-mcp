using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using Xunit;

namespace RvtMcp.Tests
{
    public class SendCodeCompilerTests
    {
        private static readonly string ApiName = "ScriptApi_" + Guid.NewGuid().ToString("N");
        private static readonly byte[] ApiBytes = CompileApi();
        private static readonly Assembly ApiAssembly = Assembly.Load(ApiBytes);
        private static IEnumerable<MetadataReference> PlatformReferences() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(new[] { typeof(JObject).Assembly.Location, typeof(McpTaskResult).Assembly.Location, typeof(ScriptInput).Assembly.Location })
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));

        private static byte[] CompileApi()
        {
            var source = @"namespace Autodesk.Revit.DB { public class Document {} }
namespace Autodesk.Revit.DB.Architecture { public class Stairs {} }
namespace Autodesk.Revit.DB.Electrical { public class CableTray {} }
namespace Autodesk.Revit.DB.Mechanical { public class Duct {} }
namespace Autodesk.Revit.DB.Plumbing { public class Pipe {} }
namespace Autodesk.Revit.DB.Structure { public enum StructuralType { NonStructural, Beam } }
namespace Autodesk.Revit.UI {
 public class UIApplication { public UIDocument ActiveUIDocument => null; }
 public class UIDocument { public Autodesk.Revit.DB.Document Document => null; }
}";
            var compilation = CSharpCompilation.Create(ApiName, new[] { CSharpSyntaxTree.ParseText(source) },
                PlatformReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            return bytes.ToArray();
        }

        internal static ScriptCompileResult Compile(ScriptInput input, params ScriptModuleRevision[] modules)
            => SendCodeCompiler.Compile(input, modules, PlatformReferences().Concat(new[] { MetadataReference.CreateFromImage(ApiBytes) }));

        internal static object Run(ScriptCompileResult result, string data = null)
        {
            Assert.True(result.Success, result.ErrorCode + ": " + string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            ResolveEventHandler resolve = (_, args) => new AssemblyName(args.Name).Name == ApiName ? ApiAssembly : null;
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            try
            {
                var script = Assembly.Load(result.AssemblyBytes);
                var app = Activator.CreateInstance(ApiAssembly.GetType("Autodesk.Revit.UI.UIApplication")!);
                return script.GetType(ScriptCompileResult.EntryType)!.GetMethod("Invoke")!.Invoke(null, new[] { app, data })!;
            }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        }

        [Fact]
        public void Data_is_bound_as_json_text_without_changing_script_source()
        {
            var json = JsonConvert.SerializeObject(new { text = "quotes \"; throw new Exception(); //\nViệt Nam 😀" });
            var input = new ScriptInput("return (string)data[\"text\"];", ScriptData.FromJson(json));
            Assert.Equal(JObject.Parse(json).Value<string>("text"), Run(Compile(input), input.Data.Json));
        }

        [Fact]
        public void Data_shim_keeps_date_like_strings_doubles_exact_integers_and_maximum_depth()
        {
            var json = "{\"ts\":\"2026-10-26T10:00:00+07:00\",\"day\":\"2026-10-26\",\"x\":1234567.123456789,\"n\":9007199254740993," +
                       "\"deep\":" + new string('[', 63) + "1" + new string(']', 63) + "}";
            var input = new ScriptInput("return data;", ScriptData.FromJson(json));
            var data = (JObject)Run(Compile(input), input.Data.Json);
            Assert.Equal(JTokenType.String, data["ts"]!.Type);
            Assert.Equal("2026-10-26T10:00:00+07:00", (string)data["ts"]!);
            Assert.Equal("2026-10-26", (string)data["day"]!);
            Assert.Equal(1234567.123456789, (double)data["x"]!);
            Assert.Equal(9007199254740993L, (long)data["n"]!);
            Assert.Equal(1, (int)data.SelectToken("deep" + string.Concat(Enumerable.Repeat("[0]", 63)))!); // 64 containers incl. root
        }

        [Fact]
        public void No_data_keeps_local_data_variable_and_home_doc_nullability()
        {
            var input = new ScriptInput("var data=7; return doc == null && uidoc == null ? data : 0;", usageContext: ScriptModes.Test);
            Assert.Equal(7, Run(Compile(input)));
            var collision = Compile(new ScriptInput(input.Code, ScriptData.FromJson("{}")));
            Assert.False(collision.Success);
            Assert.Contains(collision.Diagnostics, d => d.Source == "script" && d.Message.Contains("data"));
        }

        [Theory]
        [InlineData("string", "\"legacy\"", "legacy")]
        [InlineData("int", "7", "7")]
        [InlineData("object", "new { count=7 }", "{\"count\":7}")]
        public void Full_source_keeps_legacy_return_types(string type, string expression, string expected)
        {
            var code = "using Autodesk.Revit.UI; public class McpDynamicScript { public static " + type +
                       " Run(UIApplication app) { return " + expression + "; } }";
            var returned = Run(Compile(new ScriptInput(code, usageContext: ScriptModes.Test)));
            Assert.Equal(expected, returned is string s ? s : JToken.FromObject(returned).ToString(Formatting.None));
        }

        [Fact]
        public void Full_source_resolves_exact_mode_and_rejects_missing_or_generic_entrypoint()
        {
            const string code = @"using Autodesk.Revit.UI; using Newtonsoft.Json.Linq;
public class McpDynamicScript {
 public static object Run(UIApplication app) { return 1; }
 public static object Run(UIApplication app,JObject data) { return (int)data[""n""]; }
}";
            Assert.Equal(1, Run(Compile(new ScriptInput(code))));
            var data = new ScriptInput(code, ScriptData.FromJson("{\"n\":7}"));
            Assert.Equal(7, Run(Compile(data), data.Data.Json));
            foreach (var bad in new[] {
                "public class McpDynamicScript { public static object Run(Autodesk.Revit.UI.UIApplication app) { return null; } }",
                "public class McpDynamicScript { public static object Run<T>(Autodesk.Revit.UI.UIApplication app, Newtonsoft.Json.Linq.JObject data) { return null; } }" })
                Assert.Equal(ScriptErrorCodes.EntrypointMismatch, Compile(new ScriptInput(bad, ScriptData.FromJson("{}"))).ErrorCode);
        }

        [Fact]
        public void Task_factory_from_the_actual_contract_is_returned_by_the_shim()
        {
            var input = new ScriptInput("return McpTaskResult.Pass((int)data[\"n\"]);", ScriptData.FromJson("{\"n\":7}"), resultContract: ScriptModes.TaskV1);
            var task = Assert.IsType<McpTaskResult>(Run(Compile(input), input.Data.Json));
            Assert.True(task.IsPassed);
            Assert.Equal(7, task.Value);
        }

        [Fact]
        public void Modules_use_dependency_first_snapshots_and_separate_sources()
        {
            var helper = new ScriptModuleRevision("helper", "public static class Helper { public static int Value => 7; }");
            var input = new ScriptInput("return Helper.Value;", modules: new[] { "helper" });
            Assert.Equal(7, Run(Compile(input, helper)));
            var invalid = new ScriptModuleRevision("helper", "return 7;");
            var result = Compile(input, invalid);
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Source == "module:helper");
        }

        [Fact]
        public void Compile_only_emits_and_checks_signatures_without_running_initializers()
        {
            var marker = Path.Combine(Path.GetTempPath(), "rvt-compile-witness-" + Guid.NewGuid().ToString("N"));
            var module = new ScriptModuleRevision("helper", "public static class Helper { static Helper() { System.IO.File.WriteAllText(" +
                JsonConvert.SerializeObject(marker) + ", \"invoked\"); } public static int Value => 7; }");
            try
            {
                var check = Compile(new ScriptInput("return Helper.Value;", modules: new[] { "helper" }, compileOnly: true), module);
                Assert.True(check.Success);
                Assert.Null(check.AssemblyBytes);
                Assert.False(File.Exists(marker));
                var execute = Compile(new ScriptInput("return Helper.Value;", modules: new[] { "helper" }), module);
                Assert.Equal(7, Run(execute));
                Assert.Equal("invoked", File.ReadAllText(marker));
            }
            finally { if (File.Exists(marker)) File.Delete(marker); }
        }

        [Fact]
        public void Diagnostics_map_back_to_body_helpers_and_module_input_lines()
        {
            var body = Compile(new ScriptInput("\nvar first=1;\nreturn missing;\n", compileOnly: true));
            Assert.Contains(body.Diagnostics, d => d.Source == "script" && d.Line == 3 && d.Column == 8 && d.DiagnosticId == "CS0103");
            var declaration = Compile(new ScriptInput("return new Helper().Value;\n\npublic class Helper {\n public int Value => missing;\n}", compileOnly: true));
            Assert.Contains(declaration.Diagnostics, d => d.Source == "script" && d.Line == 4 && d.DiagnosticId == "CS0103");
            var module = new ScriptModuleRevision("helper", "public static class Helper {\n public static int Value => missing;\n}");
            var result = Compile(new ScriptInput("return Helper.Value;", modules: new[] { "helper" }, compileOnly: true), module);
            Assert.Contains(result.Diagnostics, d => d.Source == "module:helper" && d.Line == 2 && d.DiagnosticId == "CS0103");
            var defined = Compile(new ScriptInput("#define X\n\npublic class McpDynamicScript { public static object Run(Autodesk.Revit.UI.UIApplication app) => missing; }",
                resultContract: ScriptModes.TaskV1, compileOnly: true));
            Assert.Contains(defined.Diagnostics, d => d.Source == "script" && d.Line == 3 && d.DiagnosticId == "CS0103");
        }

        [Theory]
        [InlineData("global using System;\nreturn 7;")]
        [InlineData("using Autodesk = System;\nreturn 7;")]
        [InlineData("global using Autodesk = System;\nreturn 7;")]
        public void Caller_global_usings_and_aliases_compile_with_every_enhanced_option(string code)
        {
            Assert.Equal(7, Run(Compile(new ScriptInput(code, usageContext: ScriptModes.Test))));
            Assert.True(Compile(new ScriptInput(code, compileOnly: true)).Success);
            var data = new ScriptInput(code, ScriptData.FromJson("{}"));
            Assert.Equal(7, Run(Compile(data), data.Data.Json));
            var task = new ScriptInput(code.Replace("return 7;", "return McpTaskResult.Pass(7);"), resultContract: ScriptModes.TaskV1);
            Assert.Equal(7, Assert.IsType<McpTaskResult>(Run(Compile(task))).Value);
        }

        [Theory]
        [InlineData("return " + DisciplineTypes + " ? 7 : 0;")]
        [InlineData("using Autodesk.Revit.DB.Plumbing;\nreturn " + DisciplineTypes + " ? 7 : 0;")]
        public void Body_scripts_can_use_discipline_types_without_their_usings(string code)
        {
            Assert.Equal(7, Run(Compile(new ScriptInput(code, usageContext: ScriptModes.Test))));
            var data = new ScriptInput(code, ScriptData.FromJson("{}"));
            Assert.Equal(7, Run(Compile(data), data.Data.Json));
            var task = new ScriptInput(code.Replace("? 7 : 0;", "? McpTaskResult.Pass(7) : McpTaskResult.Pass(0);"), resultContract: ScriptModes.TaskV1);
            Assert.Equal(7, Assert.IsType<McpTaskResult>(Run(Compile(task))).Value);
        }

        private const string DisciplineTypes =
            "new Stairs() != null && new CableTray() != null && new Duct() != null && new Pipe() != null && StructuralType.Beam != StructuralType.NonStructural";

        [Theory]
        [InlineData("global using Autodesk.Revit.UI;\n", 9)]
        [InlineData("#define SEVEN\n", 7)]
        [InlineData("#define SEVEN\nusing Autodesk.Revit.UI;\n", 7)]
        public void Full_source_keeps_global_usings_and_symbol_definitions_ahead_of_the_task_import(string prologue, int expected)
        {
            var code = prologue + "public class McpDynamicScript { public static object Run(Autodesk.Revit.UI.UIApplication app) {\n" +
                       "#if SEVEN\nreturn McpTaskResult.Pass(7);\n#else\nreturn McpTaskResult.Pass(9);\n#endif\n} }";
            var task = Assert.IsType<McpTaskResult>(Run(Compile(new ScriptInput(code, resultContract: ScriptModes.TaskV1))));
            Assert.Equal(expected, task.Value);
        }

        [Fact]
        public void Invalid_dependency_snapshots_are_refused_before_compiling()
        {
            var a = new ScriptModuleRevision("a", "class A {}", new[] { "b" });
            var b = new ScriptModuleRevision("b", "class B {}", new[] { "a" });
            var input = new ScriptInput("return 7;", modules: new[] { "a" });
            Assert.Equal(ScriptErrorCodes.ModuleNotFound, Assert.Throws<ScriptContractException>(() => Compile(input, a)).ErrorCode);
            Assert.Equal(ScriptErrorCodes.ModuleCycle, Assert.Throws<ScriptContractException>(() => Compile(input, a, b)).ErrorCode);
            var leaf = new ScriptModuleRevision("b", "class B {}");
            Assert.Equal(ScriptErrorCodes.InputInvalid, Assert.Throws<ScriptContractException>(() => Compile(input, a, leaf)).ErrorCode);
        }

        [Fact]
        public void Shared_dependencies_do_not_hide_an_over_depth_path()
        {
            var modules = Enumerable.Range(1, ScriptLimits.DependencyDepth)
                .Select(i => new ScriptModuleRevision("m" + i, "class M" + i + " {}",
                    i == 1 ? Array.Empty<string>() : new[] { "m" + (i - 1) })).ToArray();
            var roots = new[] { "m" + ScriptLimits.DependencyDepth };
            Assert.True(Compile(new ScriptInput("return 7;", modules: roots, compileOnly: true), modules).Success);
            var tooDeep = modules.Concat(new[] { new ScriptModuleRevision("root", "class Root {}", roots) }).ToArray();
            var input = new ScriptInput("return 7;", modules: new[] { roots[0], "root" }, compileOnly: true);
            Assert.Equal(ScriptErrorCodes.ModuleLimitExceeded,
                Assert.Throws<ScriptContractException>(() => Compile(input, tooDeep)).ErrorCode);
        }

        [Fact]
        public void Trailing_preprocessor_directives_and_same_line_diagnostics_keep_input_locations()
        {
            Assert.Equal(7, Run(Compile(new ScriptInput("#if true\nreturn 7;\n#endif", usageContext: ScriptModes.Test))));
            var result = Compile(new ScriptInput("var a=1; return missing;", compileOnly: true));
            Assert.Contains(result.Diagnostics, d => d.Source == "script" && d.Line == 1 && d.Column == 17);
        }

        [Fact]
        public void A_module_cannot_supply_a_missing_script_entrypoint()
        {
            var module = new ScriptModuleRevision("helper", "public class McpDynamicScript { public static object Run(Autodesk.Revit.UI.UIApplication app) { return 7; } }");
            var input = new ScriptInput("public class Other {}", modules: new[] { "helper" }, compileOnly: true);
            Assert.Equal(ScriptErrorCodes.EntrypointMismatch, Compile(input, module).ErrorCode);
        }

        [Fact]
        public void Snapshot_refusal_does_not_mark_a_compile_attempt()
        {
            var attempted = false;
            var input = new ScriptInput("return 7;", modules: new[] {"missing"}, compileOnly: true);
            Assert.Equal(ScriptErrorCodes.ModuleNotFound, Assert.Throws<ScriptContractException>(() =>
                SendCodeCompiler.Compile(input, Array.Empty<ScriptModuleRevision>(), PlatformReferences(), () => attempted = true)).ErrorCode);
            Assert.False(attempted);
        }
    }
}
