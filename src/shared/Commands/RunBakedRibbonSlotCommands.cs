using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Commands
{
    internal static class BakedRibbonCommandRunner
    {
        public static Result ExecuteSlot(int slot, UIApplication app, ref string message)
        {
            var appInstance = App.Instance;
            var entry = appInstance?.BakedToolRuntimeCache?.GetBySlot(slot);
            if (appInstance == null || entry == null)
            {
                message = Localization.L.T("baked.slot.empty");
                return Result.Failed;
            }

            try
            {
                appInstance.ShowOrFocusBakeInboxWindow();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot01Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(1, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot02Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(2, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot03Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(3, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot04Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(4, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot05Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(5, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot06Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(6, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot07Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(7, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot08Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(8, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot09Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(9, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot10Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(10, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot11Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(11, commandData.Application, ref message); }
    [Transaction(TransactionMode.Manual)]
    public class RunBakedRibbonSlot12Command : IExternalCommand { public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => BakedRibbonCommandRunner.ExecuteSlot(12, commandData.Application, ref message); }
}
