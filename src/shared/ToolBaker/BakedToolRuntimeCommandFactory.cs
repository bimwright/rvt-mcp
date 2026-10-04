using System;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.ToolBaker
{
    public static class BakedToolRuntimeCommandFactory
    {
        public static bool TryCreate(
            string name,
            string description,
            string parametersSchema,
            string sourceCode,
            CommandDispatcher dispatcher,
            out IRevitCommand command,
            out string error)
        {
            command = null;
            error = null;

            if (!BakedToolRuntimeSource.TryParse(sourceCode, out var spec))
            {
                error = "Invalid baked runtime source.";
                return false;
            }

            if (spec.Kind == "preset")
            {
                if (string.IsNullOrWhiteSpace(spec.Tool))
                {
                    error = "Preset baked tool is missing handler tool.";
                    return false;
                }

                command = new PresetBakedCommand(
                    name,
                    description,
                    parametersSchema,
                    spec.Tool,
                    spec.FixedArgsJson,
                    dispatcher);
                return true;
            }

            if (spec.Kind == "macro")
            {
                if (spec.Sequence == null || spec.Sequence.Length == 0)
                {
                    error = "Macro baked tool is missing sequence.";
                    return false;
                }

                command = new MacroBakedCommand(
                    name,
                    description,
                    parametersSchema,
                    spec.Sequence,
                    dispatcher);
                return true;
            }

            error = "Unsupported baked runtime source kind.";
            return false;
        }

        private sealed class PresetBakedCommand : IRevitCommand
        {
            private readonly string _handlerTool;
            private readonly string _fixedArgsJson;
            private readonly CommandDispatcher _dispatcher;

            public PresetBakedCommand(
                string name,
                string description,
                string parametersSchema,
                string handlerTool,
                string fixedArgsJson,
                CommandDispatcher dispatcher)
            {
                Name = name;
                Description = description ?? string.Empty;
                ParametersSchema = string.IsNullOrWhiteSpace(parametersSchema) ? "{}" : parametersSchema;
                _handlerTool = handlerTool;
                _fixedArgsJson = string.IsNullOrWhiteSpace(fixedArgsJson) ? "{}" : fixedArgsJson;
                _dispatcher = dispatcher;
            }

            public string Name { get; }
            public string Description { get; }
            public string ParametersSchema { get; }

            public CommandResult Execute(UIApplication app, string paramsJson)
            {
                var handler = _dispatcher?.GetCommand(_handlerTool);
                if (handler == null)
                    return CommandResult.Fail("Preset handler is not available: " + _handlerTool);

                string mergedParams;
                try
                {
                    mergedParams = MergeParams(_fixedArgsJson, paramsJson);
                }
                catch (JsonException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }

                Failures.FailureControl.ThrowIfStopped();
                var result = handler.Execute(app, mergedParams);
                Failures.FailureControl.ThrowIfStopped();
                return result;
            }
        }

        private sealed class MacroBakedCommand : IRevitCommand
        {
            private readonly string[] _sequence;
            private readonly CommandDispatcher _dispatcher;

            public MacroBakedCommand(
                string name,
                string description,
                string parametersSchema,
                string[] sequence,
                CommandDispatcher dispatcher)
            {
                Name = name;
                Description = description ?? string.Empty;
                ParametersSchema = string.IsNullOrWhiteSpace(parametersSchema) ? "{}" : parametersSchema;
                _sequence = (sequence ?? Array.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                _dispatcher = dispatcher;
            }

            public string Name { get; }
            public string Description { get; }
            public string ParametersSchema { get; }

            public CommandResult Execute(UIApplication app, string paramsJson)
            {
                var results = new JArray();
                foreach (var step in _sequence)
                {
                    Failures.FailureControl.ThrowIfStopped();
                    var handler = _dispatcher?.GetCommand(step);
                    if (handler == null)
                        return CommandResult.Fail("Macro handler is not available: " + step);

                    var result = handler.Execute(app, "{}");
                    Failures.FailureControl.ThrowIfStopped();
                    if (result == null || !result.Success)
                    {
                        var error = result?.Error;
                        return CommandResult.Fail("Macro step failed: " + step + (string.IsNullOrWhiteSpace(error) ? string.Empty : " - " + error));
                    }

                    results.Add(new JObject
                    {
                        ["tool"] = step,
                        ["success"] = true
                    });
                }

                return CommandResult.Ok(new JObject { ["results"] = results });
            }
        }

        private static string MergeParams(string fixedArgsJson, string paramsJson)
        {
            var merged = ParseObject(fixedArgsJson);
            var incoming = ParseObject(paramsJson);
            foreach (var property in incoming.Properties())
                merged[property.Name] = property.Value.DeepClone();

            return merged.ToString(Formatting.None);
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new JObject();
            return JObject.Parse(json);
        }
    }
}
