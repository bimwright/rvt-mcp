using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.ToolBaker;
using RvtMcp.Scripting;

namespace RvtMcp.Plugin
{
    /// <summary>Compile outcome of a legacy send_code script or a baked tool.</summary>
    internal sealed class RoslynCompileOutput
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string[] Diagnostics { get; set; } = Array.Empty<string>();
        public byte[] AssemblyBytes { get; set; }
        public SkippedScriptReference[] Skipped { get; set; } = Array.Empty<SkippedScriptReference>();
    }

    internal sealed class ScriptCompileReply
    {
        public ScriptCompileResult Result { get; set; }
        public SkippedScriptReference[] Skipped { get; set; } = Array.Empty<SkippedScriptReference>();
        public string ContractCode { get; set; }
        public string ContractMessage { get; set; }
    }

    /// <summary>
    /// The compile entry points that use Roslyn. RoslynHost calls them; on .NET 8/10 they run in the
    /// isolated "RvtMcp.Roslyn" load context from a second copy of this assembly (issue #68), so they
    /// take and return only strings, string arrays and BCL delegates. A type of this assembly is a
    /// different type in that copy, and the copy's static state is never initialized: keep this code pure.
    /// </summary>
    public static class RoslynBridge
    {
        public static string CompileScript(string requestJson, string[] referencePaths, Action compileStarting)
        {
            var reply = new ScriptCompileReply();
            try
            {
                var request = JObject.Parse(requestJson);
                var input = new ScriptInput(request.Value<string>("code"), ScriptData.FromJson(request.Value<string>("data")),
                    request["modules"].Values<string>(), request.Value<bool>("compile_only"),
                    request.Value<string>("result_contract"), request.Value<string>("usage_context"));
                var modules = request["resolved"].Select(m => new ScriptModuleRevision(m.Value<string>("name"), m.Value<string>("code"),
                    m["requires"].Values<string>())).ToArray();
                var references = ScriptReferences.FromPaths(referencePaths, out var skipped);
                reply.Skipped = skipped;
                reply.Result = SendCodeCompiler.Compile(input, modules, references, compileStarting);
            }
            catch (ScriptContractException ex)
            {
                reply.ContractCode = ex.ErrorCode;
                reply.ContractMessage = ex.Message;
            }
            return JsonConvert.SerializeObject(reply);
        }

        public static string CompileLegacy(string code, string[] referencePaths)
        {
            var references = ScriptReferences.FromPaths(referencePaths, out var skipped);
            var syntaxTree = CSharpSyntaxTree.ParseText(SendCodeSourceBuilder.Build(code));
            var compilation = CSharpCompilation.Create(
                "McpDynamic_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            compilation = SendCodeDocumentRewriter.Apply(compilation, syntaxTree);
            return JsonConvert.SerializeObject(Emit(compilation, skipped));
        }

        public static string CompileTool(string sourceCode, string[] referencePaths)
        {
            var references = ScriptReferences.FromPaths(referencePaths, out var skipped);
            var policy = BakeCompilerPolicy.Validate(sourceCode, references);
            if (!policy.Allowed)
                return JsonConvert.SerializeObject(new RoslynCompileOutput { Error = policy.Error, Diagnostics = new[] { policy.Error }, Skipped = skipped });
            var compilation = CSharpCompilation.Create(
                "BakedTool_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                new[] { CSharpSyntaxTree.ParseText(sourceCode) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            return JsonConvert.SerializeObject(Emit(compilation, skipped));
        }

        private static RoslynCompileOutput Emit(Compilation compilation, SkippedScriptReference[] skipped)
        {
            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);
                if (result.Success) return new RoslynCompileOutput { Success = true, AssemblyBytes = ms.ToArray(), Skipped = skipped };
                var errors = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString())
                    .Take(5)
                    .ToArray();
                return new RoslynCompileOutput { Error = "Compilation failed:\n" + string.Join("\n", errors), Diagnostics = errors, Skipped = skipped };
            }
        }
    }
}
