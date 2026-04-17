using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bimwright.Plugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ShowStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Instance == null) return Result.Failed;

            var transport = App.Instance.Transport;
            var log = App.Instance.SessionLog;
            var running = App.Instance.IsTransportRunning;

            var status = running ? "Running" : "Stopped";
            var connectionInfo = running ? transport.ConnectionInfo : "N/A";
            var client = transport?.IsClientConnected == true ? "Connected" : "No client";
            var lastCmd = transport?.LastCommandTime?.ToString("HH:mm:ss") ?? "None";
            var cmdCount = log?.Count ?? 0;

            var td = new TaskDialog("Bimwright Status")
            {
                MainInstruction = $"MCP Server: {status}",
                MainContent =
                    $"Connection: {connectionInfo}\n" +
                    $"Client: {client}\n" +
                    $"Last command: {lastCmd}\n" +
                    $"Commands this session: {cmdCount}",
                MainIcon = running
                    ? TaskDialogIcon.TaskDialogIconInformation
                    : TaskDialogIcon.TaskDialogIconWarning
            };
            td.Show();

            return Result.Succeeded;
        }
    }
}
