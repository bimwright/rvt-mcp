using System.Reflection;
using Autodesk.Revit.UI;

namespace Bimwright.Rvt.Plugin
{
    public class RibbonResult
    {
        public PushButton ToggleButton { get; set; }
        public PushButton HistoryButton { get; set; }
    }

    public static class RibbonSetup
    {
        private const string TabName = "BIMwright";
        private const string PanelName = "BIMwright";

        public static RibbonResult Create(UIControlledApplication application)
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var panel = ResolvePanel(application);

            var toggleData = new PushButtonData(
                "ToggleMcp", "MCP: ON",
                assemblyPath,
                "Bimwright.Rvt.Plugin.Commands.ToggleMcpCommand")
            {
                LargeImage = IconGenerator.McpOn32,
                Image = IconGenerator.McpOn16,
                ToolTip = "Start/Stop MCP Server"
            };

            var historyData = new PushButtonData(
                "ShowHistory", "History (0)",
                assemblyPath,
                "Bimwright.Rvt.Plugin.Commands.ShowHistoryCommand")
            {
                LargeImage = IconGenerator.History32,
                Image = IconGenerator.History16,
                ToolTip = "Show MCP command history"
            };

            var stack = panel.AddStackedItems(toggleData, historyData);

            return new RibbonResult
            {
                ToggleButton = stack[0] as PushButton,
                HistoryButton = stack[1] as PushButton
            };
        }

        private static RibbonPanel ResolvePanel(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* already created */ }
            return application.CreateRibbonPanel(TabName, PanelName);
        }
    }
}
