using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SendCodeToRevitHandler : IRevitCommand
    {
        public string Name => "send_code_to_revit";
        public string Description => "Send C# code to Revit to compile and execute dynamically";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""code"":{""type"":""string"",""description"":""C# code to compile and execute in Revit""}},""required"":[""code""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = JObject.Parse(paramsJson);
            var code = request.Value<string>("code");

            if (string.IsNullOrWhiteSpace(code))
                return Fail("code parameter is required.");

            try
            {
                var fullCode = SendCodeSourceBuilder.Build(code);
                var syntaxTree = CSharpSyntaxTree.ParseText(fullCode);

                // Gather references from loaded assemblies (safe for any .NET runtime).
                // File.Exists guards stale add-ins whose dll was deleted after Revit
                // loaded them into the AppDomain (assembly Location points nowhere).
                var references = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
                    .GroupBy(a => a.GetName().Name)
                    .Select(g => g.OrderByDescending(a => a.GetName().Version).First())
                    .Select(a => MetadataReference.CreateFromFile(a.Location))
                    .Cast<MetadataReference>()
                    .ToArray();

                var compilation = CSharpCompilation.Create(
                    "McpDynamic_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                using (var ms = new MemoryStream())
                {
                    var compileResult = compilation.Emit(ms);

                    if (!compileResult.Success)
                    {
                        var errors = compileResult.Diagnostics
                            .Where(d => d.Severity == DiagnosticSeverity.Error)
                            .Select(d => d.ToString())
                            .Take(5)
                            .ToArray();
                        return Fail("Compilation failed:\n" + string.Join("\n", errors));
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    var assembly = Assembly.Load(ms.ToArray());
                    var type = assembly.GetType("McpDynamicScript");

                    if (type == null)
                        return Fail("Class 'McpDynamicScript' not found. Ensure your code defines this class with a static Run(UIApplication) method.");

                    var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                    if (method == null)
                        return Fail("Method 'Run(UIApplication)' not found in McpDynamicScript.");

                    var output = method.Invoke(null, new object[] { app });
                    // Live wire returns raw output so the agent can read structured JSON
                    // (anonymous objects serialize properly, strings stay strings, numbers stay numbers).
                    // Persistence paths (McpLogger, McpSessionLog, JournalEntry, bake suggestions, usage
                    // events) call BakeRedactor independently at write time, so logs/bakes remain redacted.
                    return CommandResult.Ok(new
                    {
                        executed = true,
                        result = output
                    });
                }
            }
            catch (TargetInvocationException ex)
            {
                return Fail($"Runtime error: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                return Fail($"Error: {ex.Message}");
            }
        }

        private static CommandResult Fail(string error)
        {
            return CommandResult.Fail(McpResponsePrivacy.RedactErrorForResponse(error));
        }
    }
}
