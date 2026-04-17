using System;
using Autodesk.Revit.UI;

namespace Bimwright.Plugin
{
    public class IdlingUpdater
    {
        private readonly RibbonResult _ribbon;
        private DateTime _lastUpdate = DateTime.MinValue;
        private bool _lastRunning;
        private int _lastCount;

        public IdlingUpdater(RibbonResult ribbon)
        {
            _ribbon = ribbon;
        }

        public void Update(bool isRunning, ITransportServer transport, McpSessionLog sessionLog)
        {
            if (_ribbon == null) return;

            var now = DateTime.Now;
            if ((now - _lastUpdate).TotalMilliseconds < 1000) return;
            _lastUpdate = now;

            var count = sessionLog?.Count ?? 0;

            // Only update UI if state changed
            if (isRunning != _lastRunning)
            {
                _lastRunning = isRunning;

                if (isRunning)
                {
                    _ribbon.ToggleButton.ItemText = "MCP: ON";
                    _ribbon.ToggleButton.LargeImage = IconGenerator.McpOn32;
                    _ribbon.ToggleButton.Image = IconGenerator.McpOn16;

                    var client = transport.IsClientConnected ? "Connected" : "Waiting";
                    var lastCmd = transport.LastCommandTime?.ToString("HH:mm:ss") ?? "None";
                    _ribbon.ToggleButton.ToolTip =
                        $"MCP Server running on {transport.ConnectionInfo}\n" +
                        $"Client: {client}\n" +
                        $"Last command: {lastCmd}\n" +
                        $"Click to stop";
                }
                else
                {
                    _ribbon.ToggleButton.ItemText = "MCP: OFF";
                    _ribbon.ToggleButton.LargeImage = IconGenerator.McpOff32;
                    _ribbon.ToggleButton.Image = IconGenerator.McpOff16;
                    _ribbon.ToggleButton.ToolTip = "MCP Server stopped\nClick to start";
                }
            }
            else if (isRunning)
            {
                // Update tooltip for client/lastCmd changes even if running state didn't change
                var client = transport.IsClientConnected ? "Connected" : "Waiting";
                var lastCmd = transport.LastCommandTime?.ToString("HH:mm:ss") ?? "None";
                _ribbon.ToggleButton.ToolTip =
                    $"MCP Server running on {transport.ConnectionInfo}\n" +
                    $"Client: {client}\n" +
                    $"Last command: {lastCmd}\n" +
                    $"Click to stop";
            }

            if (count != _lastCount)
            {
                _lastCount = count;
                _ribbon.HistoryButton.ItemText = $"History ({count})";
            }
        }
    }
}
