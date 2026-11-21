using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ToggleToastCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Instance == null)
                return Result.Failed;

            // While toasts are paused for today, the button means "show them again", not "turn them off".
            if (App.Instance.ToastEnabled && Views.Toast.ToastSnooze.IsActive())
            {
                App.Instance.ToastNotifier?.ResumeFromPause();
                return Result.Succeeded;
            }

            App.Instance.ToastEnabled = !App.Instance.ToastEnabled;
            if (App.Instance.Config != null)
                App.Instance.Config.EnableToast = App.Instance.ToastEnabled;

            string saveError;
            var persisted = RvtMcpConfig.TrySaveEnableToast(
                App.Instance.ToastEnabled, out saveError);
            if (App.Instance.ToastEnabled && !Views.Toast.ToastSnooze.ResumeIfPaused(out var resumeError))
            {
                persisted = false;
                saveError = resumeError;
            }
            if (!persisted && !string.IsNullOrWhiteSpace(saveError))
                App.DebugLog("Toast preference could not be saved: " + saveError);

            App.Instance.ToastNotifier?.OnToastEnabledChanged(
                App.Instance.ToastEnabled, persisted);

            return Result.Succeeded;
        }
    }
}
