using System;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.ToolBaker;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ApplyBakeSuggestionHandler : IRevitCommand
    {
        private readonly BakedToolRuntimeCache _runtimeCache;

        public ApplyBakeSuggestionHandler(BakedToolRuntimeCache runtimeCache = null)
        {
            _runtimeCache = runtimeCache;
        }

        public string Name => "apply_bake";
        public string Description => "Compile, smoke-test, and register an accepted baked tool inside the Revit plugin runtime.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""suggestion_id"":{""type"":""string""},""tool_name"":{""type"":""string""},""display_name"":{""type"":""string""},""description"":{""type"":""string""},""output_choice"":{""type"":""string""},""source"":{""type"":""string""},""source_code"":{""type"":""string""},""params_schema"":{""type"":""string""}},""required"":[""suggestion_id"",""tool_name"",""source_code"",""params_schema""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try
            {
                request = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Ok(Failure("invalid_payload", ex.Message, new JObject()));
            }

            var toolName = (string)request["tool_name"];
            var displayName = (string)request["display_name"] ?? toolName;
            var description = (string)request["description"] ?? string.Empty;
            var outputChoice = (string)request["output_choice"] ?? "mcp_only";
            var source = (string)request["source"] ?? string.Empty;
            var sourceCodeInput = (string)request["source_code"];
            var paramsSchema = (string)request["params_schema"] ?? "{}";

            if (string.IsNullOrWhiteSpace(toolName))
                return CommandResult.Ok(Failure("invalid_payload", "tool_name is required.", new JObject()));
            if (string.IsNullOrWhiteSpace(sourceCodeInput))
                return CommandResult.Ok(Failure("invalid_payload", "source_code is required.", new JObject()));

            var dispatcher = App.Instance?.CommandDispatcher;
            if (dispatcher == null)
                return CommandResult.Ok(Failure("runtime_unavailable", "CommandDispatcher is not available.", new JObject()));

            var finalSource = sourceCodeInput;
            IRevitCommand command;
            byte[] assemblyBytes = null;
            var hasRuntimeMarker = BakedToolRuntimeSource.HasMarker(sourceCodeInput);
            if (hasRuntimeMarker && !BakedToolRuntimeSource.IsAllowedForSuggestionSource(source))
            {
                return CommandResult.Ok(Failure(
                    "compile_failed",
                    "Baked runtime markers are only allowed for preset and macro suggestions.",
                    new JObject { ["source"] = source }));
            }

            if (hasRuntimeMarker)
            {
                if (!BakedToolRuntimeCommandFactory.TryCreate(
                    toolName,
                    description,
                    paramsSchema,
                    sourceCodeInput,
                    dispatcher,
                    out command,
                    out var runtimeError))
                {
                    return CommandResult.Ok(Failure(
                        "compile_failed",
                        runtimeError ?? "Invalid baked runtime source.",
                        new JObject()));
                }
            }
            else
            {
                finalSource = NormalizeSource(toolName, description, paramsSchema, sourceCodeInput);
                var compile = ToolCompiler.Compile(finalSource);
                if (!compile.Success || compile.Command == null)
                {
                    return CommandResult.Ok(Failure(
                        "compile_failed",
                        compile.Error ?? "Compilation failed.",
                        new JObject
                        {
                            ["diagnostics"] = new JArray(compile.Diagnostics ?? new string[0])
                        }));
                }

                if (!string.Equals(compile.Command.Name, toolName, StringComparison.Ordinal))
                {
                    return CommandResult.Ok(Failure(
                        "compile_failed",
                        "Compiled command name did not match requested tool_name.",
                        new JObject
                        {
                            ["requested_tool_name"] = toolName,
                            ["compiled_tool_name"] = compile.Command.Name
                        }));
                }

                command = compile.Command;
                assemblyBytes = compile.AssemblyBytes;
            }

            if (!string.Equals(command.Name, toolName, StringComparison.Ordinal))
            {
                return CommandResult.Ok(Failure(
                    "compile_failed",
                    "Runtime command name did not match requested tool_name.",
                    new JObject
                    {
                        ["requested_tool_name"] = toolName,
                        ["compiled_tool_name"] = command.Name
                    }));
            }

            var smoke = SmokeExecute(app, command, toolName, paramsSchema);
            if (!smoke.Success)
                return CommandResult.Ok(Failure("smoke_failed", smoke.Message, smoke.Diagnostics));

            if (!dispatcher.RegisterBaked(command))
                return CommandResult.Ok(Failure("runtime_registration_failed", "Baked tool name collides with an existing command.", new JObject { ["tool_name"] = toolName }));

            App.Instance?.BakedToolRegistry?.Save(new BakedToolMeta
            {
                Name = toolName,
                DisplayName = displayName,
                Description = description,
                Source = source,
                OutputChoice = outputChoice,
                ParametersSchema = paramsSchema,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("o"),
                CallCount = 0
            }, finalSource);

            if (OutputIncludesRibbon(outputChoice))
            {
                var cache = _runtimeCache ?? App.Instance?.BakedToolRuntimeCache;
                cache?.RegisterOrUpdate(new BakedToolRuntimeEntry(
                    toolName,
                    displayName,
                    description,
                    paramsSchema,
                    outputChoice,
                    command));
                App.Instance?.RefreshBakedRibbonButtons();
            }

            return CommandResult.Ok(new
            {
                success = true,
                tool_name = toolName,
                display_name = displayName,
                description,
                output_choice = outputChoice,
                source,
                source_code_sha256 = Sha256Hex(Encoding.UTF8.GetBytes(finalSource ?? string.Empty)),
                source_code_byte_count = Encoding.UTF8.GetByteCount(finalSource ?? string.Empty),
                params_schema = paramsSchema,
                dll_sha256 = assemblyBytes == null ? null : Sha256Hex(assemblyBytes),
                dll_byte_count = assemblyBytes?.Length ?? 0,
                revit_version = AuthToken.RevitVersion
            });
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes ?? new byte[0]);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                    builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        private static string NormalizeSource(string toolName, string description, string paramsSchema, string sourceCode)
        {
            if (sourceCode.IndexOf("IRevitCommand", StringComparison.Ordinal) >= 0 &&
                sourceCode.IndexOf("class", StringComparison.Ordinal) >= 0)
                return sourceCode;

            return ToolCompiler.WrapInCommand(toolName, description ?? string.Empty, paramsSchema ?? "{}", sourceCode);
        }

        private static SmokeResult SmokeExecute(UIApplication app, IRevitCommand command, string toolName, string paramsSchema)
        {
            TransactionGroup group = null;
            var started = false;
            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null)
                    return SmokeResult.Fail("Active Revit document is required for bake smoke test.", new JObject());

                group = new TransactionGroup(doc, "BakeSmokeTest_" + toolName);
                group.Start();
                started = true;

                var dummyParamsJson = BakedToolParameterDefaults.BuildDummyParamsJson(paramsSchema);
                var result = command.Execute(app, dummyParamsJson);
                if (result == null)
                    return SmokeResult.Fail("Baked command returned null.", new JObject { ["dummy_params_json"] = dummyParamsJson });
                if (!result.Success)
                    return SmokeResult.Fail(
                        string.IsNullOrWhiteSpace(result.Error) ? "Baked command returned CommandResult.Fail." : result.Error,
                        new JObject { ["dummy_params_json"] = dummyParamsJson, ["command_error"] = result.Error ?? string.Empty });

                return SmokeResult.Pass();
            }
            catch (Exception ex)
            {
                return SmokeResult.Fail(ex.Message, new JObject { ["exception_type"] = ex.GetType().FullName });
            }
            finally
            {
                if (started && group != null)
                {
                    try { group.RollBack(); }
                    catch { }
                }
            }
        }

        private static bool OutputIncludesRibbon(string outputChoice)
        {
            return (outputChoice ?? string.Empty).IndexOf("ribbon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object Failure(string code, string message, JObject diagnostics)
        {
            return new
            {
                success = false,
                error_code = code,
                message = message ?? code,
                diagnostics = diagnostics ?? new JObject()
            };
        }

        private sealed class SmokeResult
        {
            public bool Success { get; private set; }
            public string Message { get; private set; }
            public JObject Diagnostics { get; private set; }

            public static SmokeResult Pass()
            {
                return new SmokeResult { Success = true, Diagnostics = new JObject() };
            }

            public static SmokeResult Fail(string message, JObject diagnostics)
            {
                return new SmokeResult { Success = false, Message = message, Diagnostics = diagnostics ?? new JObject() };
            }
        }
    }
}
