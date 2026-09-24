using System;
using System.IO;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CopyConnectionInfoCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Instance == null) return Result.Failed;

            var transport = App.Instance.Transport;
            var ver = AuthToken.RevitVersion ?? "2022";
            var discoveryFile = Path.Combine(AuthToken.DiscoveryDir(), AuthToken.DiscoveryFileName(ver));

            var info = App.Instance.IsTransportRunning
                ? transport.ConnectionInfo
                : Localization.L.T("dialog.connectionInfo.notRunning");

            Clipboard.SetText(info);

            var td = new TaskDialog(Localization.L.T("dialog.connectionInfo.title"))
            {
                MainInstruction = Localization.L.T("dialog.connectionInfo.copied"),
                MainContent = $"{info}\n" + Localization.L.T("dialog.connectionInfo.discoveryFile", ("path", discoveryFile))
            };
            td.Show();

            return Result.Succeeded;
        }
    }
}
