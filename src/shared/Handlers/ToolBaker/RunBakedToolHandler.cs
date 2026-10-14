using System;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.ToolBaker;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class RunBakedToolHandler : IRevitCommand
    {
        public string Name => "run_baked_tool";
        public string Description => "Execute a baked tool by name";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""params"":{""type"":""object""},""output"":{""type"":""string"",""enum"": [""inline"", ""file""],""default"":""inline""}},""required"":[""name""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = JObject.Parse(paramsJson);
            var name = request.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.Fail("name is required.");

            var dispatcher = App.Instance?.CommandDispatcher;
            if (dispatcher == null)
                return CommandResult.Fail("CommandDispatcher not available.");

            var registry = App.Instance?.BakedToolRegistry;
            var command = dispatcher.GetBakedCommand(name);
            if (!BakedToolDispatchAuthorizer.TryAuthorize(registry, name, command != null, out var authError))
                return CommandResult.Fail(authError);

            var toolParams = request["params"]?.ToString() ?? "{}";
            var meta = registry.GetMeta(name);
            if (!BakedToolDispatchAuthorizer.TryValidateParameters(meta, toolParams, out var paramError))
                return CommandResult.Fail(paramError);

            var revitVersion = AuthToken.RevitVersion ?? string.Empty;
            BakedToolDispatchAuthorizer.TryGetCompatWarning(meta, revitVersion, out var compatWarning);

            CommandResult result;
            try
            {
                Failures.FailureControl.ThrowIfStopped();
                result = command.Execute(app, toolParams);
            }
            catch (Exception) when (Failures.FailureControl.IsStopped)
            {
                result = CommandResult.Fail("Failure policy stopped the baked tool.");
            }
            if (Failures.FailureControl.IsStopped)
                result = CommandResult.Fail("Failure policy stopped the baked tool; inspect its failure and operation records before retrying.");
            registry.RecordRun(name, revitVersion, result != null && result.Success, result?.Error);

            if (result == null)
                return CommandResult.Fail("Baked tool returned null.");
            if (!result.Success)
                return result;

            return CommandResult.Ok(new
            {
                tool_name = name,
                revit_version = revitVersion,
                compat_warning = compatWarning,
                result = result.Data
            });
        }
    }
}
