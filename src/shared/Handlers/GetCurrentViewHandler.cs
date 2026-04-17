using Autodesk.Revit.UI;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class GetCurrentViewHandler : IRevitCommand
    {
        public string Name => "get_current_view_info";
        public string Description => "Get current active view info";
        public string ParametersSchema => "{}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var view = doc.ActiveView;
            if (view == null)
                return CommandResult.Fail("No active view.");

            return CommandResult.Ok(new
            {
                viewId = RevitCompat.GetId(view.Id),
                viewName = view.Name,
                viewType = view.ViewType.ToString(),
                levelName = view.GenLevel?.Name,
                levelId = RevitCompat.GetIdOrNull(view.GenLevel?.Id),
                scale = view.Scale,
                detailLevel = view.DetailLevel.ToString(),
                displayStyle = view.DisplayStyle.ToString()
            });
        }
    }
}
