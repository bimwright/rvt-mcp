using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Commands
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
                var otherMcps = McpAddinDiscovery.FindOtherMcpAddins(AuthToken.RevitVersion, App.DebugLog);
                if (otherMcps.Count > 0)
                {
                    var notice = new TaskDialog(Localization.L.T("mcp.coexistence.title"))
                    {
                        MainInstruction = Localization.L.T("mcp.coexistence.notice", ("count", otherMcps.Count + 1)),
                        MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                        CommonButtons = TaskDialogCommonButtons.Ok
                    };
                    if (notice.Show() != TaskDialogResult.Ok) return Result.Cancelled;
                }
                App.Instance.CreateAndStartTransport();
            }

            var enabled = App.Instance.IsTransportRunning;
            string saveError;
            if (!RvtMcpConfig.TrySaveAutoStartTransport(enabled, out saveError))
            {
                App.DebugLog("MCP startup preference could not be saved: " + saveError);
                message = Localization.L.T("toast.status.saveFailed");
                return Result.Failed;
            }
            if (App.Instance.Config != null)
                App.Instance.Config.AutoStartTransport = enabled;

            return Result.Succeeded;
        }
    }
}
