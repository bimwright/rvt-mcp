using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class SendCodeToRevitHandler : IRevitCommand
    {
        public string Name => "send_code_to_revit";
        public string Description => "Send C# code to Revit to compile and execute dynamically";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""code"":{""type"":""string"",""description"":""C# code to compile and execute in Revit""}},""required"":[""code""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
#if !ALLOW_SEND_CODE
            return CommandResult.Fail(
                "send_code_to_revit is disabled in this build. " +
                "It is only available in Debug builds with ALLOW_SEND_CODE defined.");
#else
            var request = JObject.Parse(paramsJson);
            var code = request.Value<string>("code");

            if (string.IsNullOrWhiteSpace(code))
                return CommandResult.Fail("code parameter is required.");

            // Runtime confirmation dialog
            var preview = code.Length > 500 ? code.Substring(0, 500) + "\n...(truncated)" : code;
            var dlg = new Autodesk.Revit.UI.TaskDialog("Revit MCP — Execute dynamic code?")
            {
                MainInstruction = "A tool wants to compile and run C# inside Revit.",
                MainContent = "Code preview:\n\n" + preview,
                CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Yes
                              | Autodesk.Revit.UI.TaskDialogCommonButtons.No,
                DefaultButton = Autodesk.Revit.UI.TaskDialogResult.No
            };
            var result = dlg.Show();
            if (result != Autodesk.Revit.UI.TaskDialogResult.Yes)
                return CommandResult.Fail("User denied dynamic code execution.");

            // Wrap user code in a class if it doesn't contain one
            var fullCode = code;
            if (!code.Contains("class "))
            {
                fullCode = @"
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public class McpDynamicScript
{
    public static object Run(UIApplication app)
    {
        var doc = app.ActiveUIDocument.Document;
        var uidoc = app.ActiveUIDocument;
        " + code + @"
    }
}";
            }

            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(fullCode);

                // Gather references from loaded assemblies (safe for any .NET runtime)
                var references = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
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
                        return CommandResult.Fail("Compilation failed:\n" + string.Join("\n", errors));
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    var assembly = Assembly.Load(ms.ToArray());
                    var type = assembly.GetType("McpDynamicScript");

                    if (type == null)
                        return CommandResult.Fail("Class 'McpDynamicScript' not found. Ensure your code defines this class with a static Run(UIApplication) method.");

                    var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                    if (method == null)
                        return CommandResult.Fail("Method 'Run(UIApplication)' not found in McpDynamicScript.");

                    var output = method.Invoke(null, new object[] { app });
                    return CommandResult.Ok(new
                    {
                        executed = true,
                        result = output?.ToString() ?? "(null)"
                    });
                }
            }
            catch (TargetInvocationException ex)
            {
                return CommandResult.Fail($"Runtime error: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Error: {ex.Message}");
            }
#endif
        }
    }
}
