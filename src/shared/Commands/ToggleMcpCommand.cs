using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bimwright.Rvt.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleMcpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Instance == null) return Result.Failed;

            if (App.Instance.IsTransportRunning)
            {
                App.Instance.StopTransport();
            }
            else
            {
                App.Instance.CreateAndStartTransport();
            }

            return Result.Succeeded;
        }
    }
}
