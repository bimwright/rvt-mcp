using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Scripting;
using System.Collections.Generic;

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

            if (ScriptWireRequest.HasEnhancedOptions(request))
                return Fail(ScriptErrorCodes.CapabilityRequired + ": Enhanced script requests require a matching gateway envelope.");

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
                compilation = SendCodeDocumentRewriter.Apply(compilation, syntaxTree);

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
                        Failures.FailureControl.NotifyCompileFailed();
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

                    object output;
                    using (McpDocument.Begin(app))
                        output = method.Invoke(null, new object[] { app });
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

        internal CommandResult ExecuteProtocol(UIApplication app, string parameters, JObject metadata, ScriptExecutionState state)
        {
            if (!ScriptProtocol.IsReady)
            {
                Failures.FailureControl.NotifyCompileFailed();
                return Fail(ScriptErrorCodes.CapabilityRequired + ": Script protocol is not enabled in this add-in.");
            }
            try
            {
                var request = ScriptWireRequest.Parse(parameters);
                request.ValidateMetadata(metadata);
                return ExecuteEnhanced(app, request.Input, request.Modules, state);
            }
            catch (ScriptContractException ex) { Failures.FailureControl.NotifyCompileFailed(); return Fail(ex.ErrorCode + ": " + ex.Message); }
            catch (Exception ex) { Failures.FailureControl.NotifyCompileFailed(); return Fail(ScriptErrorCodes.InputInvalid + ": " + ex.Message); }
        }

        internal CommandResult ExecuteEnhanced(UIApplication app, ScriptInput input, IReadOnlyList<ScriptModuleRevision> modules, ScriptExecutionState state = null)
        {
            try
            {
                var references = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
                    .GroupBy(a => a.GetName().Name)
                    .Select(g => g.Key == "RevitAPIUI" ? typeof(UIApplication).Assembly :
                        g.Key == "RevitAPI" ? typeof(Autodesk.Revit.DB.Document).Assembly : g.OrderByDescending(a => a.GetName().Version).First())
                    .Select(a => MetadataReference.CreateFromFile(a.Location)).Cast<MetadataReference>().ToArray();
                var compiled = SendCodeCompiler.Compile(input, modules, references, () => state?.Compiling());
                state?.CompileFinished();
                if (!compiled.Success)
                {
                    state?.Failed();
                    Failures.FailureControl.NotifyCompileFailed();
                    return new CommandResult { Success = false,
                        Error = compiled.ErrorCode + ": " + string.Join("\n", compiled.Diagnostics.Select(d =>
                            McpResponsePrivacy.RedactErrorForResponse(d.Source + ":" + d.Line + ":" + d.Column + " " + d.DiagnosticId + " " + d.Message))),
                        Data = new { compiled = false, compile_only = input.CompileOnly, executed = false,
                            diagnostics = compiled.Diagnostics.Select(d => new {
                                source = McpResponsePrivacy.RedactErrorForResponse(d.Source), line = d.Line, column = d.Column,
                                diagnostic_id = d.DiagnosticId, message = McpResponsePrivacy.RedactErrorForResponse(d.Message) }).ToArray() } };
                }
                if (input.CompileOnly)
                {
                    state?.Compiled();
                    return CommandResult.Ok(new { compiled = true, compile_only = true, executed = false,
                        diagnostics = Array.Empty<object>(), module_signatures = compiled.ModuleSignatures,
                        compile_host = ScriptCompileHost.Capture(int.Parse(app.Application.VersionNumber, System.Globalization.CultureInfo.InvariantCulture)) });
                }
                var assembly = Assembly.Load(compiled.AssemblyBytes);
                var method = assembly.GetType(ScriptCompileResult.EntryType).GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(UIApplication), typeof(string) }, null);
                object output;
                state?.Invoking();
                using (McpDocument.Begin(app)) output = method.Invoke(null, new object[] { app, input.Data.Json });
                output = state == null ? output : state.Complete(output);
                return CommandResult.Ok(new { executed = true, result = output });
            }
            catch (ScriptContractException ex)
            {
                if (state?.InvokeStarted != true) Failures.FailureControl.NotifyCompileFailed();
                state?.Failed(); return Fail(ex.ErrorCode + ": " + ex.Message);
            }
            catch (TargetInvocationException ex) { state?.Failed(); return Fail("Runtime error: " + (ex.InnerException?.Message ?? ex.Message)); }
            catch (Exception ex) { state?.Failed(); return Fail("Error: " + ex.Message); }
        }

        private static CommandResult Fail(string error)
        {
            return CommandResult.Fail(McpResponsePrivacy.RedactErrorForResponse(error));
        }
    }
}
