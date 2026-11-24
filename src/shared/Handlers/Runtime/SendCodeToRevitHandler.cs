using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
                // Gather references from loaded assemblies (safe for any .NET runtime).
                // File.Exists guards stale add-ins whose dll was deleted after Revit
                // loaded them into the AppDomain (assembly Location points nowhere).
                var compiled = RoslynHost.CompileLegacy(code, ReferencePaths(RoslynHost.LoadedAssemblies()
                    .Where(a => File.Exists(a.Location))
                    .GroupBy(a => a.GetName().Name)
                    .Select(g => g.OrderByDescending(a => a.GetName().Version).First())));
                var skipped = LogSkipped(compiled.Skipped);

                if (!compiled.Success)
                {
                    Failures.FailureControl.NotifyCompileFailed();
                    return Fail(compiled.Error + SkippedNote(skipped));
                }

                var assembly = Assembly.Load(compiled.AssemblyBytes);
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
                if (skipped.Length != 0)
                    return CommandResult.Ok(new { executed = true, result = output, skipped_references = SkippedDto(skipped) });
                return CommandResult.Ok(new
                {
                    executed = true,
                    result = output
                });
            }
            catch (TargetInvocationException ex)
            {
                return Fail($"Runtime error: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (ScriptContractException ex)
            {
                Failures.FailureControl.NotifyCompileFailed();
                return Fail(ex.ErrorCode + ": " + ex.Message);
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
            catch (Exception ex)
            {
                Failures.FailureControl.NotifyCompileFailed();
                // A compiler that cannot load is a host problem, not bad input (issue #68).
                var unavailable = RoslynHost.CompilerUnavailable(ex);
                return Fail(unavailable != null ? unavailable.ErrorCode + ": " + unavailable.Message : ScriptErrorCodes.InputInvalid + ": " + ex.Message);
            }
        }

        internal CommandResult ExecuteEnhanced(UIApplication app, ScriptInput input, IReadOnlyList<ScriptModuleRevision> modules, ScriptExecutionState state = null)
        {
            try
            {
                var references = ReferencePaths(RoslynHost.LoadedAssemblies()
                    .Where(a => File.Exists(a.Location))
                    .GroupBy(a => a.GetName().Name)
                    .Select(g => g.Key == "RevitAPIUI" ? typeof(UIApplication).Assembly :
                        g.Key == "RevitAPI" ? typeof(Autodesk.Revit.DB.Document).Assembly : g.OrderByDescending(a => a.GetName().Version).First()));
                var compiled = RoslynHost.CompileScript(input, modules, references, () => state?.Compiling(), out var skippedReferences);
                var skipped = LogSkipped(skippedReferences);
                state?.CompileFinished();
                if (!compiled.Success)
                {
                    state?.Failed();
                    Failures.FailureControl.NotifyCompileFailed();
                    return new CommandResult { Success = false,
                        Error = compiled.ErrorCode + ": " + (compiled.Hint == null ? "" : compiled.Hint + "\n") + string.Join("\n", compiled.Diagnostics.Select(d =>
                            McpResponsePrivacy.RedactErrorForResponse(d.Source + ":" + d.Line + ":" + d.Column + " " + d.DiagnosticId + " " + d.Message
                                + (d.Hint == null ? "" : "\n  hint: " + d.Hint))))
                            + SkippedNote(skipped),
                        Data = new { compiled = false, compile_only = input.CompileOnly, executed = false,
                            diagnostics = compiled.Diagnostics.Select(d => new {
                                source = McpResponsePrivacy.RedactErrorForResponse(d.Source), line = d.Line, column = d.Column,
                                diagnostic_id = d.DiagnosticId, message = McpResponsePrivacy.RedactErrorForResponse(d.Message),
                                hint = d.Hint == null ? null : McpResponsePrivacy.RedactErrorForResponse(d.Hint) }).ToArray() } };
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
                if (compiled.Repair != null)
                {
                    var repairedResult = new System.Collections.Generic.Dictionary<string, object> { ["executed"] = true, ["result"] = output, ["script_repair"] = compiled.Repair };
                    if (skipped.Length != 0) repairedResult["skipped_references"] = SkippedDto(skipped);
                    return CommandResult.Ok(repairedResult);
                }
                if (skipped.Length != 0)
                    return CommandResult.Ok(new { executed = true, result = output, skipped_references = SkippedDto(skipped) });
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

        // Roslyn is not touched here: RoslynHost compiles in its own load context (issue #68).
        private static string[] ReferencePaths(IEnumerable<Assembly> assemblies) => assemblies.Select(a => a.Location).ToArray();

        // Issue #38: a loaded add-in DLL whose metadata Roslyn cannot read is skipped instead of failing every compile.
        private static SkippedScriptReference[] LogSkipped(SkippedScriptReference[] skipped)
        {
            skipped = skipped ?? Array.Empty<SkippedScriptReference>();
            foreach (var skip in skipped)
            {
                try { DiagnosticLog.Write("SendCodeReferences", $"Skipped reference {skip.Name} ({skip.Path}): {skip.Reason}", AuthToken.RevitVersion); }
                catch { /* diagnostic path must not break compilation */ }
            }
            return skipped;
        }

        private static object[] SkippedDto(SkippedScriptReference[] skipped) =>
            skipped.Select(s => (object)new { name = s.Name, reason = s.Reason }).ToArray();

        private static string SkippedNote(SkippedScriptReference[] skipped) =>
            skipped.Length == 0 ? string.Empty
                : "\nSkipped references (unreadable metadata): " + string.Join(", ", skipped.Select(s => s.Name));

        private static CommandResult Fail(string error)
        {
            return CommandResult.Fail(McpResponsePrivacy.RedactErrorForResponse(error));
        }
    }
}
