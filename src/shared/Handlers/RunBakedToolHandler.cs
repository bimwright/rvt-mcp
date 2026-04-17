using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class RunBakedToolHandler : IRevitCommand
    {
        public string Name => "run_baked_tool";
        public string Description => "Execute a baked tool by name";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""params"":{""type"":""object""}},""required"":[""name""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
#if !ALLOW_SEND_CODE
            return CommandResult.Fail("Baked tools are disabled in this build.");
#else
            var request = JObject.Parse(paramsJson);
            var name = request.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.Fail("name is required.");

            var dispatcher = App.Instance?.CommandDispatcher;
            if (dispatcher == null)
                return CommandResult.Fail("CommandDispatcher not available.");

            var command = dispatcher.GetCommand(name);
            if (command == null)
                return CommandResult.Fail($"Baked tool '{name}' not found. Use list_baked_tools to see available tools.");

            // Increment usage counter
            App.Instance.BakedToolRegistry?.IncrementCallCount(name);

            // Forward params to the baked tool
            var toolParams = request["params"]?.ToString() ?? "{}";
            return command.Execute(app, toolParams);
#endif
        }
    }
}
