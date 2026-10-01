using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    // Only the history query asks for this context; ordinary read responses stay unchanged.
    public sealed class GetChangeHistoryContextHandler : IRevitCommand
    {
        public string Name => "get_change_records";
        public string Description => "Resolve the active model's opaque history key for the gateway's local query.";
        public string ParametersSchema => "{\"type\":\"object\",\"properties\":{}}";
        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var model = McpChangeTracker.Scope.Identity(app.ActiveUIDocument?.Document);
            return CommandResult.Ok(new { modelKey = (string)model?["key"] });
        }
    }
}
