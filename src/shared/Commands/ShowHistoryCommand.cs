using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bimwright.Rvt.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ShowHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Instance == null) return Result.Failed;
            App.Instance.ShowOrFocusHistoryWindow();
            return Result.Succeeded;
        }
    }
}
