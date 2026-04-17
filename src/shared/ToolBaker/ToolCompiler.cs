using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bimwright.Rvt.Plugin.ToolBaker
{
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
using Bimwright.Rvt.Plugin;

public class BakedTool_{safeName} : IRevitCommand
{{
    public string Name => ""{name}"";
    public string Description => ""{escapedDesc}"";
    public string ParametersSchema => @""{escapedSchema}"";

    public CommandResult Execute(UIApplication app, string paramsJson)
    {{
        try
        {{
            var doc = app.ActiveUIDocument.Document;
            var uidoc = app.ActiveUIDocument;
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
            error = null;
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

                var references = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                    .GroupBy(a => a.GetName().Name)
                    .Select(g => g.OrderByDescending(a => a.GetName().Version).First())
                    .Select(a => MetadataReference.CreateFromFile(a.Location))
                    .Cast<MetadataReference>()
                    .ToArray();

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
                        error = "Compilation failed:\n" + string.Join("\n", errors);
                        return null;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    var assembly = Assembly.Load(ms.ToArray());

                    // Find the IRevitCommand implementation
                    var commandType = assembly.GetTypes()
                        .FirstOrDefault(t => typeof(IRevitCommand).IsAssignableFrom(t) && !t.IsAbstract);

                    if (commandType == null)
                    {
                        error = "Compiled assembly does not contain an IRevitCommand implementation.";
                        return null;
                    }

                    return (IRevitCommand)Activator.CreateInstance(commandType);
                }
            }
            catch (Exception ex)
            {
                error = $"Compilation error: {ex.Message}";
                return null;
            }
        }
    }
}
