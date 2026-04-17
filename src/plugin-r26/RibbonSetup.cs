using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitMcp.Plugin
{
    public class RibbonResult
    {
        public PushButton ToggleButton { get; set; }
        public PushButton HistoryButton { get; set; }
    }

    public static class RibbonSetup
    {
        private const string KeiTabName = "KEI-ME";
        private const string TabName = "BIMwright";
        private const string PanelName = "BIMwright";

        public static RibbonResult Create(UIControlledApplication application)
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var panel = ResolvePanel(application);

            var toggleData = new PushButtonData(
                "ToggleMcp", "MCP: ON",
                assemblyPath,
                "RevitMcp.Plugin.Commands.ToggleMcpCommand")
            {
                LargeImage = IconGenerator.McpOn32,
                Image = IconGenerator.McpOn16,
                ToolTip = "Start/Stop MCP Server"
            };

            var historyData = new PushButtonData(
                "ShowHistory", "History (0)",
                assemblyPath,
                "RevitMcp.Plugin.Commands.ShowHistoryCommand")
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

        // Own BIMwright tab (lands at end via tab creation order) when KEI-ME is already
        // registered at MCP load time; else fall back to the built-in Add-Ins tab.
        private static RibbonPanel ResolvePanel(UIControlledApplication application)
        {
            if (KeiTabRegistered(application))
            {
                try { application.CreateRibbonTab(TabName); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                return application.CreateRibbonPanel(TabName, PanelName);
            }
            return application.CreateRibbonPanel(PanelName);
        }

        private static bool KeiTabRegistered(UIControlledApplication application)
        {
            try
            {
                application.GetRibbonPanels(KeiTabName);
                return true;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                return false;
            }
        }
    }
}
