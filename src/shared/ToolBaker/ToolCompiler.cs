using System;
using System.Linq;
using System.Reflection;
using RvtMcp.Scripting;

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
                var groups = RoslynHost.LoadedAssemblies()
                    .GroupBy(a => a.GetName().Name)
                    .ToList();

                // Log when a simple-name appears multiple times in the AppDomain — silent dedupe upgrades
                // can make a baked tool compile against a newer API than the author tested.
                LogAssemblyConflicts(groups);

                // Roslyn runs in its own load context (issue #68); loading the tool stays here.
                var compiled = RoslynHost.CompileTool(sourceCode, groups
                    .Select(g => g.OrderByDescending(a => a.GetName().Version).First().Location).ToArray());
                // Issue #38: skip loaded DLLs whose metadata Roslyn cannot read instead of failing every compile.
                LogSkippedReferences(compiled.Skipped ?? new SkippedScriptReference[0]);
                if (!compiled.Success)
                {
                    return new ToolCompileResult
                    {
                        Success = false,
                        Error = compiled.Error,
                        Diagnostics = compiled.Diagnostics ?? new string[0]
                    };
                }

                var bytes = compiled.AssemblyBytes;
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
            catch (ScriptContractException ex)
            {
                return new ToolCompileResult
                {
                    Success = false,
                    Error = ex.ErrorCode + ": " + ex.Message,
                    Diagnostics = new[] { ex.ErrorCode + ": " + ex.Message }
                };
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

        private static void LogSkippedReferences(SkippedScriptReference[] skipped)
        {
            foreach (var skip in skipped)
            {
                try { DiagnosticLog.Write("CompileReferences", $"Bake-compile skipped reference {skip.Name} ({skip.Path}): {skip.Reason}", AuthToken.RevitVersion); }
                catch { /* diagnostic path must not break compilation */ }
            }
        }

        private static void LogAssemblyConflicts(System.Collections.Generic.List<IGrouping<string, Assembly>> groups)
        {
            try
            {
                var conflicts = groups.Where(g => g.Count() > 1).ToList();
                if (conflicts.Count == 0) return;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{DateTime.UtcNow:o} Bake-compile assembly dedupe conflicts:");
                foreach (var g in conflicts)
                {
                    var versions = g.Select(a => a.GetName().Version?.ToString() ?? "?").ToArray();
                    var picked = g.OrderByDescending(a => a.GetName().Version).First().GetName().Version?.ToString() ?? "?";
                    sb.AppendLine($"  {g.Key}: versions=[{string.Join(", ", versions)}] → picked {picked}");
                }
                DiagnosticLog.Write("CompileReferences", sb.ToString(), AuthToken.RevitVersion);
            }
            catch { /* diagnostic path must not break compilation */ }
        }
    }
}
