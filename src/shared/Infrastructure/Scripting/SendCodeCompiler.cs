using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RvtMcp.Scripting;

namespace RvtMcp.Plugin
{
    internal sealed class ScriptCompileDiagnostic
    {
        public string Source { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string DiagnosticId { get; set; }
        public string Message { get; set; }
    }

    internal sealed class ScriptCompileResult
    {
        public bool Success { get; set; }
        public string ErrorCode { get; set; }
        public ScriptCompileDiagnostic[] Diagnostics { get; set; } = Array.Empty<ScriptCompileDiagnostic>();
        public byte[] AssemblyBytes { get; set; }
        public Dictionary<string, string[]> ModuleSignatures { get; set; } = new Dictionary<string, string[]>(StringComparer.Ordinal);
        public const string EntryType = "__RvtMcpSendCodeEntry";
    }

    /// <summary>Prepare/compile/metadata validation only. Never loads an assembly or invokes user code.</summary>
    internal static class SendCodeCompiler
    {
        internal static ScriptCompileResult Compile(ScriptInput input, IReadOnlyList<ScriptModuleRevision> modules,
            IEnumerable<MetadataReference> references, Action compileStarting = null)
        {
            modules = modules ?? Array.Empty<ScriptModuleRevision>();
            ValidateSnapshot(input.Modules, modules);
            compileStarting?.Invoke();
            var trees = new List<SyntaxTree>();
            foreach (var module in modules)
            {
                var tree = CSharpSyntaxTree.ParseText(module.Code, path: "module:" + module.Name);
                var topLevel = tree.GetCompilationUnitRoot().Members.OfType<GlobalStatementSyntax>().FirstOrDefault();
                if (topLevel != null)
                {
                    var position = topLevel.GetLocation().GetLineSpan().StartLinePosition;
                    return Error(ScriptErrorCodes.CompileFailed, "Module source must contain declarations only.", "module:" + module.Name,
                        position.Line + 1, position.Character + 1);
                }
                trees.Add(tree);
            }
            trees.Add(CSharpSyntaxTree.ParseText(SendCodeSourceBuilder.BuildEnhanced(input.Code, input.Data.IsProvided,
                input.ResultContract == ScriptModes.TaskV1), path: "script"));
            var compilation = CSharpCompilation.Create("McpDynamic_" + Guid.NewGuid().ToString("N"), trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            // Bind signatures from symbols; do not instantiate types/read fields/run initializers.
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) return Diagnostics(errors);
            var script = compilation.GetTypeByMetadataName("McpDynamicScript");
            var signatures = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var tree in trees.Where(t => t.FilePath.StartsWith("module:", StringComparison.Ordinal)))
            {
                var model = compilation.GetSemanticModel(tree);
                signatures[tree.FilePath.Substring(7)] = tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
                    .Select(node => model.GetDeclaredSymbol(node)).Where(symbol => symbol?.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public)
                    .Select(symbol => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
                    .Where(text => text.Length <= 512).Distinct(StringComparer.Ordinal).Take(200).ToArray();
            }
            var appType = compilation.GetTypeByMetadataName("Autodesk.Revit.UI.UIApplication");
            var dataType = compilation.GetTypeByMetadataName("Newtonsoft.Json.Linq.JObject");
            var methods = script?.GetMembers("Run").OfType<IMethodSymbol>().Where(method => method.IsStatic &&
                method.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public && !method.IsGenericMethod &&
                method.DeclaringSyntaxReferences.Any(r => r.SyntaxTree.FilePath == "script") &&
                method.Parameters.Length == (input.Data.IsProvided ? 2 : 1) &&
                method.Parameters.All(p => p.RefKind == RefKind.None) &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, appType) &&
                (!input.Data.IsProvided || SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, dataType))).ToArray();
            if (methods == null || methods.Length != 1)
                return Error(ScriptErrorCodes.EntrypointMismatch, "Expected exactly one public static Run matching the supplied data mode.", "script");

            var invocation = "global::McpDynamicScript.Run(app" + (input.Data.IsProvided ? ", Data(dataJson)" : "") + ")";
            // Date-like strings stay strings; fraction/exponent numbers read as double, kept finite by ScriptData.
            // global:: keeps a caller's global using alias from capturing these references.
            var dataReader = !input.Data.IsProvided ? "" :
                " private static global::Newtonsoft.Json.Linq.JObject Data(string json) { using (var reader = new global::Newtonsoft.Json.JsonTextReader(new global::System.IO.StringReader(json))" +
                " { DateParseHandling = global::Newtonsoft.Json.DateParseHandling.None, FloatParseHandling = global::Newtonsoft.Json.FloatParseHandling.Double })" +
                " return global::Newtonsoft.Json.Linq.JObject.Load(reader); }";
            var shim = "#line hidden\npublic static class " + ScriptCompileResult.EntryType +
                       " { public static object Invoke(global::Autodesk.Revit.UI.UIApplication app, string dataJson) { " +
                       (methods[0].ReturnsVoid ? invocation + "; return null;" : "return " + invocation + ";") + " }" + dataReader + " }";
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(shim, path: "generated:entry"));
            foreach (var tree in trees) compilation = SendCodeDocumentRewriter.Apply(compilation, tree);
            using (var bytes = new MemoryStream())
            {
                var emitted = compilation.Emit(bytes);
                if (!emitted.Success) return Diagnostics(emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
                return new ScriptCompileResult { Success = true, AssemblyBytes = input.CompileOnly ? null : bytes.ToArray(), ModuleSignatures = signatures };
            }
        }

        private static ScriptCompileResult Diagnostics(IEnumerable<Diagnostic> diagnostics)
            => new ScriptCompileResult {
                ErrorCode = ScriptErrorCodes.CompileFailed,
                Diagnostics = diagnostics.Take(20).Select(d => {
                    var span = d.Location.GetMappedLineSpan();
                    return new ScriptCompileDiagnostic { Source = span.Path, Line = span.StartLinePosition.Line + 1,
                        Column = span.StartLinePosition.Character + 1, DiagnosticId = d.Id, Message = d.GetMessage() };
                }).ToArray()
            };

        private static ScriptCompileResult Error(string code, string message, string source, int line = 1, int column = 1)
            => new ScriptCompileResult { ErrorCode = code, Diagnostics = new[] { new ScriptCompileDiagnostic {
                Source = source, Line = line, Column = column, DiagnosticId = code, Message = message } } };

        private static void ValidateSnapshot(IEnumerable<string> roots, IReadOnlyList<ScriptModuleRevision> modules)
        {
            if (modules.Any(m => m == null))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Missing resolved module snapshot.");
            if (modules.Count > ScriptLimits.ModuleCount || modules.Sum(m => (long)ScriptIdentity.Utf8Bytes(m.Code)) > ScriptLimits.ResolvedModuleBytes)
                throw new ScriptContractException(ScriptErrorCodes.ModuleLimitExceeded, "Resolved modules exceed their budget.");
            if (modules.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != modules.Count)
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Duplicate resolved modules.");
            var byName = modules.ToDictionary(m => m.Name, StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var heights = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            Action<string, int> visit = null;
            visit = (name, depth) => {
                if (depth > ScriptLimits.DependencyDepth)
                    throw new ScriptContractException(ScriptErrorCodes.ModuleLimitExceeded, "Module dependency graph is too deep.");
                if (visited.Contains(name))
                {
                    if (depth + heights[name] - 1 > ScriptLimits.DependencyDepth)
                        throw new ScriptContractException(ScriptErrorCodes.ModuleLimitExceeded, "Module dependency graph is too deep.");
                    return;
                }
                if (!byName.TryGetValue(name, out var module))
                    throw new ScriptContractException(ScriptErrorCodes.ModuleNotFound, "Resolved module dependency is missing.");
                if (!visiting.Add(name)) throw new ScriptContractException(ScriptErrorCodes.ModuleCycle, "Module dependency cycle.");
                foreach (var dependency in module.Requires) visit(dependency, depth + 1);
                heights[name] = 1 + module.Requires.Select(dependency => heights[dependency]).DefaultIfEmpty(0).Max();
                visiting.Remove(name); visited.Add(name); order.Add(name);
            };
            foreach (var root in roots) visit(root, 1);
            if (!order.SequenceEqual(modules.Select(m => m.Name), StringComparer.Ordinal))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Resolved modules do not match the ordered root snapshot.");
        }
    }
}
