namespace RvtMcp.Plugin.Views
{
    public sealed partial class HistoryWindow
    {
        public HistoryWindow(McpSessionLog log, CommandDispatcher commands, McpEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent)
            : this(new Infrastructure.UI.HistoryRuntime(log, commands, handler, externalEvent)) { }
    }
}
