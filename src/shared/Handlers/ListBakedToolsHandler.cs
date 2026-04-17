using System.Linq;
using Autodesk.Revit.UI;

namespace RevitMcp.Plugin.Handlers
{
    public class ListBakedToolsHandler : IRevitCommand
    {
        public string Name => "list_baked_tools";
        public string Description => "List all baked (user-compiled) tools with usage stats";
        public string ParametersSchema => "{}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
#if !ALLOW_SEND_CODE
            return CommandResult.Fail("Baked tools are disabled in this build.");
#else
            var registry = App.Instance?.BakedToolRegistry;
            if (registry == null)
                return CommandResult.Ok(new { tools = new object[0] });

            var tools = registry.GetAll().Select(m => new
            {
                name = m.Name,
                description = m.Description,
                parametersSchema = m.ParametersSchema,
                createdUtc = m.CreatedUtc,
                callCount = m.CallCount
            }).ToArray();

            return CommandResult.Ok(new { count = tools.Length, tools });
#endif
        }
    }
}
