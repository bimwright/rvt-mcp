using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Views.Settings;

namespace RvtMcp.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ShowSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Instance == null) return Result.Failed;
            App.Instance.ShowOrFocusSettingsWindow(SettingsTab.General);
            return Result.Succeeded;
        }
    }
}
