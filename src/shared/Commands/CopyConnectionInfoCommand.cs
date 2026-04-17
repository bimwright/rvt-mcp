using System;
using System.IO;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bimwright.Rvt.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CopyConnectionInfoCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Instance == null) return Result.Failed;

            var transport = App.Instance.Transport;
            var ver = AuthToken.RevitVersion ?? "R22";
            var kind = (ver == "R25" || ver == "R26" || ver == "R27") ? "pipe" : "port";
            var portFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", $"{kind}{ver}.txt");

            var info = App.Instance.IsTransportRunning
                ? transport.ConnectionInfo
                : "MCP Server is not running";

            Clipboard.SetText(info);

            var td = new TaskDialog("Connection Info")
            {
                MainInstruction = "Copied to clipboard",
                MainContent = $"{info}\nPort file: {portFile}"
            };
            td.Show();

            return Result.Succeeded;
        }
    }
}
