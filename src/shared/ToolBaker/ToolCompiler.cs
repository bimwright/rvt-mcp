using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RvtMcp.Plugin.ToolBaker
{
    public sealed class ToolCompileResult
    {
        public bool Success { get; set; }
        public IRevitCommand Command { get; set; }
        public string Error { get; set; }
        public string[] Diagnostics { get; set; } = new string[0];
        public byte[] AssemblyBytes { get; set; }
    }

    public static class ToolCompiler
    {
        /// <summary>
        /// Wraps user code body in an IRevitCommand class.
        /// The code has access to: app (UIApplication), doc (Document), uidoc (UIDocument), request (JObject from paramsJson).
        /// Code must return a value (used as CommandResult.Ok data) or throw (caught as CommandResult.Fail).
        /// </summary>
        public static string WrapInCommand(string name, string description, string parametersSchema, string codeBody)
        {
            var safeName = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            var escapedDesc = description.Replace("\"", "\\\"");
            var escapedSchema = (parametersSchema ?? "{}").Replace("\"", "\"\"");

            return $@"
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

public class BakedTool_{safeName} : IRevitCommand
{{
    public string Name => ""{name}"";
    public string Description => ""{escapedDesc}"";
    public string ParametersSchema => @""{escapedSchema}"";

    public CommandResult Execute(UIApplication app, string paramsJson)
    {{
        try
        {{
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            var request = JObject.Parse(paramsJson ?? ""{{}}"");

            {codeBody}
        }}
        catch (Exception ex)
        {{
            return CommandResult.Fail(ex.Message);
        }}
    }}
}}";
        }

        /// <summary>
        /// Compiles source code to an in-memory assembly and returns the IRevitCommand instance.
        /// Returns null and sets error if compilation fails.
        /// </summary>
        public static IRevitCommand CompileAndLoad(string sourceCode, out string error)
        {
            byte[] assemblyBytes;
            return CompileAndLoad(sourceCode, out error, out assemblyBytes);
        }

        public static IRevitCommand CompileAndLoad(string sourceCode, out string error, out byte[] assemblyBytes)
        {
            var result = Compile(sourceCode);
            error = result.Error;
            assemblyBytes = result.AssemblyBytes;
            return result.Command;
        }

        public static ToolCompileResult Compile(string sourceCode)
        {
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

                var groups = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                    .GroupBy(a => a.GetName().Name)
                    .ToList();

                // Log when a simple-name appears multiple times in the AppDomain — silent dedupe upgrades
                // can make a baked tool compile against a newer API than the author tested.
                LogAssemblyConflicts(groups);

                var references = groups
                    .Select(g => g.OrderByDescending(a => a.GetName().Version).First())
                    .Select(a => MetadataReference.CreateFromFile(a.Location))
                    .Cast<MetadataReference>()
                    .ToArray();

                var policy = BakeCompilerPolicy.Validate(sourceCode, references);
                if (!policy.Allowed)
                {
                    return new ToolCompileResult
                    {
                        Success = false,
                        Error = policy.Error,
                        Diagnostics = new[] { policy.Error }
                    };
                }

                var compilation = CSharpCompilation.Create(
                    "BakedTool_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                using (var ms = new MemoryStream())
                {
                    var result = compilation.Emit(ms);
                    if (!result.Success)
                    {
                        var errors = result.Diagnostics
                            .Where(d => d.Severity == DiagnosticSeverity.Error)
                            .Select(d => d.ToString())
                            .Take(5)
                            .ToArray();
                        return new ToolCompileResult
                        {
                            Success = false,
                            Error = "Compilation failed:\n" + string.Join("\n", errors),
                            Diagnostics = errors
                        };
                    }

                    var bytes = ms.ToArray();
                    var assembly = Assembly.Load(bytes);

                    // Find the IRevitCommand implementation
                    var commandType = assembly.GetTypes()
                        .FirstOrDefault(t => typeof(IRevitCommand).IsAssignableFrom(t) && !t.IsAbstract);

                    if (commandType == null)
                    {
                        return new ToolCompileResult
                        {
                            Success = false,
                            Error = "Compiled assembly does not contain an IRevitCommand implementation.",
                            Diagnostics = new[] { "No IRevitCommand implementation was found." },
                            AssemblyBytes = bytes
                        };
                    }

                    return new ToolCompileResult
                    {
                        Success = true,
                        Command = (IRevitCommand)Activator.CreateInstance(commandType),
                        AssemblyBytes = bytes
                    };
                }
            }
            catch (Exception ex)
            {
                return new ToolCompileResult
                {
                    Success = false,
                    Error = $"Compilation error: {ex.Message}",
                    Diagnostics = new[] { ex.GetType().Name + ": " + ex.Message }
                };
            }
        }

        private static void LogAssemblyConflicts(System.Collections.Generic.List<IGrouping<string, Assembly>> groups)
        {
            try
            {
                var conflicts = groups.Where(g => g.Count() > 1).ToList();
                if (conflicts.Count == 0) return;

                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RvtMcp", "baked");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "compile-refs.log");

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{DateTime.UtcNow:o} Bake-compile assembly dedupe conflicts:");
                foreach (var g in conflicts)
                {
                    var versions = g.Select(a => a.GetName().Version?.ToString() ?? "?").ToArray();
                    var picked = g.OrderByDescending(a => a.GetName().Version).First().GetName().Version?.ToString() ?? "?";
                    sb.AppendLine($"  {g.Key}: versions=[{string.Join(", ", versions)}] → picked {picked}");
                }
                File.AppendAllText(logPath, sb.ToString());
            }
            catch { /* diagnostic path must not break compilation */ }
        }
    }
}
