using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Bimwright.Rvt.Plugin.ToolBaker;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class BakeToolHandler : IRevitCommand
    {
        public string Name => "bake_tool";
        public string Description => "Compile and register a new permanent tool from C# code";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""description"":{""type"":""string""},""code"":{""type"":""string""},""parametersSchema"":{""type"":""string""}},""required"":[""name"",""description"",""code""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
#if !ALLOW_SEND_CODE
            return CommandResult.Fail("bake_tool is disabled in this build.");
#else
            var request = JObject.Parse(paramsJson);
            var name = request.Value<string>("name");
            var description = request.Value<string>("description");
            var code = request.Value<string>("code");
            var schema = request.Value<string>("parametersSchema") ?? "{}";

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
                return CommandResult.Fail("name and code are required.");

            // Validate name: alphanumeric + underscores only
            foreach (var c in name)
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return CommandResult.Fail($"Invalid tool name '{name}'. Use only letters, digits, underscores.");

            var registry = App.Instance?.BakedToolRegistry;
            if (registry == null)
                return CommandResult.Fail("BakedToolRegistry not initialized.");

            // User confirmation
            var dlg = new TaskDialog("Revit MCP \u2014 Bake new tool?")
            {
                MainInstruction = $"Bake tool: {name}",
                MainContent = $"Description: {description}\n\nCode preview:\n{(code.Length > 300 ? code.Substring(0, 300) + "..." : code)}",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (dlg.Show() != TaskDialogResult.Yes)
                return CommandResult.Fail("User denied bake operation.");

            // Wrap and compile
            var sourceCode = ToolCompiler.WrapInCommand(name, description, schema, code);
            var command = ToolCompiler.CompileAndLoad(sourceCode, out var error);
            if (command == null)
                return CommandResult.Fail(error);

            // Save and register
            var meta = new BakedToolMeta
            {
                Name = name,
                Description = description,
                ParametersSchema = schema,
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                CallCount = 0
            };
            registry.Save(meta, sourceCode);

            // Register in dispatcher (overwrites if exists)
            App.Instance.CommandDispatcher.Register(command);

            return CommandResult.Ok(new
            {
                baked = true,
                name,
                description,
                message = $"Tool '{name}' baked and registered. Use run_baked_tool to call it."
            });
#endif
        }
    }
}
