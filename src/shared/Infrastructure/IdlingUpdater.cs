using System;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin
{
    public class IdlingUpdater
    {
        private readonly RibbonResult _ribbon;
        private readonly Views.Toast.McpToastNotifier _toastNotifier;
        private DateTime _lastUpdate = DateTime.MinValue;
        private bool _lastRunning;
        private bool _wasClientConnected;
        private int _lastCount;
        private bool _lastToastEnabled;

        public IdlingUpdater(RibbonResult ribbon, Views.Toast.McpToastNotifier toastNotifier = null)
        {
            _ribbon = ribbon;
            _toastNotifier = toastNotifier;
        }

        public void Update(bool isRunning, ITransportServer transport, McpSessionLog sessionLog, bool toastEnabled)
        {
            if (_ribbon == null) return;

            var now = DateTime.Now;
            if ((now - _lastUpdate).TotalMilliseconds < 1000) return;
            _lastUpdate = now;

            // Rising edge: first client attach (and each re-attach) confirms
            // the agent↔plugin wire end-to-end.
            var connected = isRunning && transport != null && transport.IsClientConnected;
            if (connected && !_wasClientConnected)
                _toastNotifier?.OnClientConnected(transport.ConnectionInfo);
            _wasClientConnected = connected;

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

            if (_ribbon.ToastButton != null && toastEnabled != _lastToastEnabled)
            {
                _lastToastEnabled = toastEnabled;
                if (toastEnabled)
                {
                    _ribbon.ToastButton.ItemText = "Toast: ON";
                    _ribbon.ToastButton.LargeImage = IconGenerator.ToastOn32;
                    _ribbon.ToastButton.Image = IconGenerator.ToastOn16;
                    _ribbon.ToastButton.ToolTip = "MCP activity toasts enabled\nShows top-left notifications when AI tools run\nClick to disable";
                }
                else
                {
                    _ribbon.ToastButton.ItemText = "Toast: OFF";
                    _ribbon.ToastButton.LargeImage = IconGenerator.ToastOff32;
                    _ribbon.ToastButton.Image = IconGenerator.ToastOff16;
                    _ribbon.ToastButton.ToolTip = "MCP activity toasts disabled\nClick to enable top-left AI activity notifications";
                }
            }
        }
    }
}
