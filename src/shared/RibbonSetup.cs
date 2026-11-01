using System;
using System.Reflection;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.ToolBaker;

namespace RvtMcp.Plugin
{
    public class RibbonResult
    {
        public PushButton ToggleButton { get; set; }
        public PushButton HistoryButton { get; set; }
        public PushButton ToastButton { get; set; }
        public PushButton ReportsButton { get; set; }
        public PushButton BakeInboxButton { get; set; }
        public PushButton SettingsButton { get; set; }
        public PushButton LanguageButton { get; set; }
    }

    public static class RibbonSetup
    {
        private const string PanelName = "RvtMcp";
        private static readonly HashSet<string> CreatedButtons = new HashSet<string>();
        private static object _styledRibbon;
        private static int _styledLanguage = -1, _styleAttempts;
        /// <summary>
        /// Revit routes every item added after <c>AddSlideOut()</c> into the
        /// slide-out — so ordering is contractual: all main-panel items first,
        /// then AddSlideOut, then the Language command; baked-tool
        /// buttons added later land below those commands in the slide-out.
        /// </summary>
        public static RibbonResult Create(UIControlledApplication application, RvtMcpConfig config = null, BakedToolRuntimeCache runtimeCache = null)
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var panel = ResolvePanel(application);

            var toggleData = new PushButtonData(
                "ToggleMcp", L.T("ribbon.toggle.text.stopped"),
                assemblyPath,
                "RvtMcp.Plugin.Commands.ToggleMcpCommand")
            {
                LargeImage = IconGenerator.McpOff32,
                Image = IconGenerator.McpOff16,
                ToolTip = L.T("ribbon.toggle.tooltip.stopped")
            };

            var historyData = new PushButtonData(
                "ShowHistory", L.T("ribbon.history.text", ("count", 0)),
                assemblyPath,
                "RvtMcp.Plugin.Commands.ShowHistoryCommand")
            {
                LargeImage = IconGenerator.History32,
                Image = IconGenerator.History16,
                ToolTip = L.T("ribbon.history.tooltip")
            };

            var toastEnabled = config?.EnableToastOrDefault == true;
            var toastData = new PushButtonData(
                "ToggleToast", L.T("ribbon.toast.text"),
                assemblyPath,
                "RvtMcp.Plugin.Commands.ToggleToastCommand")
            {
                LargeImage = toastEnabled ? IconGenerator.ToastOn32 : IconGenerator.ToastOff32,
                Image = toastEnabled ? IconGenerator.ToastOn16 : IconGenerator.ToastOff16,
                ToolTip = toastEnabled
                    ? L.T("ribbon.toast.tooltip.enabled")
                    : L.T("ribbon.toast.tooltip.disabled")
            };

            var stack = panel.AddStackedItems(toggleData, historyData, toastData);
            var reportStack = panel.AddStackedItems(ReportsData(assemblyPath), SettingsData(assemblyPath));
            var reportsButton = reportStack[0] as PushButton;
            var settingsButton = reportStack[1] as PushButton;
            CreatedButtons.Add("ShowReports");
            CreatedButtons.Add("ShowSettings");
            EnsureReportsStackIcons();
            PushButton bakeInboxButton = null;
            if (config?.EnableAdaptiveBakeOrDefault == true)
                bakeInboxButton = AddBakeInboxButton(panel, assemblyPath);

            panel.AddSlideOut();
            var languageButton = AddLanguageButton(panel, assemblyPath);

            // Baked-tool buttons must come AFTER the slide-out — they intentionally
            // appear below the Language command (there is no API slot
            // that puts them back in the main panel once a slide-out exists).
            if (config?.EnableAdaptiveBakeOrDefault == true)
                AddOrUpdateBakedToolButtons(application, runtimeCache);

            return new RibbonResult
            {
                ToggleButton = stack[0] as PushButton,
                HistoryButton = stack[1] as PushButton,
                ToastButton = stack[2] as PushButton,
                ReportsButton = reportsButton,
                BakeInboxButton = bakeInboxButton,
                SettingsButton = settingsButton,
                LanguageButton = languageButton
            };
        }

        public static void AddOrUpdateBakedToolButtons(UIControlledApplication application, BakedToolRuntimeCache runtimeCache)
        {
            if (application == null || runtimeCache == null)
                return;

            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var panel = ResolvePanel(application);
            foreach (var entry in runtimeCache.GetRibbonEntries())
            {
                if (entry.RibbonSlot <= 0 || entry.RibbonSlot > BakedToolRuntimeCache.MaxRibbonSlots)
                    continue;

                var buttonName = "BakedToolSlot" + entry.RibbonSlot.ToString("00");
                if (CreatedButtons.Contains(buttonName))
                    continue;

                var data = new PushButtonData(
                    buttonName,
                    ShortLabel(entry.DisplayName),
                    assemblyPath,
                    "RvtMcp.Plugin.Commands.RunBakedRibbonSlot" + entry.RibbonSlot.ToString("00") + "Command")
                {
                    LargeImage = IconGenerator.Info32,
                    Image = IconGenerator.Info16,
                    ToolTip = entry.Description
                };

                try
                {
                    panel.AddItem(data);
                    CreatedButtons.Add(buttonName);
                }
                catch
                {
                    CreatedButtons.Add(buttonName);
                }
            }
        }

        private static PushButtonData ReportsData(string assemblyPath)
        {
            const string buttonName = "ShowReports";
            var data = new PushButtonData(
                buttonName,
                L.T("ribbon.reports.text"),
                assemblyPath,
                "RvtMcp.Plugin.Commands.ShowReportsCommand")
            {
                LargeImage = Views.ReportAssets.Report,
                Image = Views.ReportAssets.Bitmap(Views.ReportAssets.Report, 16),
                ToolTip = L.T("ribbon.reports.tooltip")
            };
            return data;
        }

        private static PushButtonData SettingsData(string assemblyPath)
        {
            const string buttonName = "ShowSettings";
            var data = new PushButtonData(
                buttonName,
                L.T("ribbon.settings.text"),
                assemblyPath,
                "RvtMcp.Plugin.Commands.ShowSettingsCommand")
            {
                LargeImage = Views.ReportAssets.Settings,
                Image = Views.ReportAssets.Bitmap(Views.ReportAssets.Settings, 16),
                ToolTip = L.T("ribbon.settings.tooltip"),
                LongDescription = L.T("ribbon.settings.tooltip")
            };
            return data;
        }

        internal static void EnsureReportsStackIcons()
        {
            try
            {
                var assembly = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "AdWindows");
                var root = assembly?.GetType("Autodesk.Windows.ComponentManager")?.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                if (root == null || ReferenceEquals(root, _styledRibbon) && _styledLanguage == L.Version) return;
                var reports = Ribbon.RibbonIconOnly.Apply(root, PanelName, "ShowReports");
                var settings = Ribbon.RibbonIconOnly.Apply(root, PanelName, "ShowSettings");
                if (reports == null || settings == null)
                {
                    if (++_styleAttempts == 10) App.DebugLog("Reports icon-only ribbon layout is unavailable; command labels remain accessible.");
                    return;
                }
                if (reports is System.Windows.DependencyObject reportControl)
                    System.Windows.Automation.AutomationProperties.SetName(reportControl, L.T("ribbon.reports.text"));
                if (settings is System.Windows.DependencyObject settingsControl)
                    System.Windows.Automation.AutomationProperties.SetName(settingsControl, L.T("ribbon.settings.text"));
                _styledRibbon = root;
                _styledLanguage = L.Version;
            }
            catch (Exception ex) { if (++_styleAttempts == 10) App.DebugLog("Reports ribbon styling failed: " + ex.Message); }
        }

        private static PushButton AddLanguageButton(RibbonPanel panel, string assemblyPath)
        {
            const string buttonName = "ShowSettingsLanguage";
            if (CreatedButtons.Contains(buttonName)) return null;
            var data = new PushButtonData(
                buttonName,
                Views.Settings.InvariantLanguageText.RibbonCaption,
                assemblyPath,
                "RvtMcp.Plugin.Commands.ShowSettingsLanguageCommand")
            {
                LargeImage = IconGenerator.Language32,
                Image = IconGenerator.Language16,
                ToolTip = L.T("ribbon.language.tooltip"),
                LongDescription = L.T("ribbon.language.tooltip")
            };
            return AddPushButton(panel, data, buttonName);
        }

        private static PushButton AddPushButton(RibbonPanel panel, PushButtonData data, string buttonName)
        {
            try
            {
                var button = panel.AddItem(data) as PushButton;
                CreatedButtons.Add(buttonName);
                return button;
            }
            catch
            {
                CreatedButtons.Add(buttonName);
                return null;
            }
        }

        private static PushButton AddBakeInboxButton(RibbonPanel panel, string assemblyPath)
        {
            const string buttonName = "ShowBakeInbox";
            if (CreatedButtons.Contains(buttonName))
                return null;

            var data = new PushButtonData(
                buttonName,
                L.T("ribbon.bakeInbox.text"),
                assemblyPath,
                "RvtMcp.Plugin.Commands.ShowBakeInboxCommand")
            {
                LargeImage = IconGenerator.Info32,
                Image = IconGenerator.Info16,
                ToolTip = L.T("ribbon.bakeInbox.tooltip")
            };

            try
            {
                var button = panel.AddItem(data) as PushButton;
                CreatedButtons.Add(buttonName);
                return button;
            }
            catch
            {
                CreatedButtons.Add(buttonName);
                return null;
            }
        }

        private static string ShortLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return L.T("ribbon.bakedTool.fallback");
            return label.Length <= 18 ? label : label.Substring(0, 18);
        }

        private static RibbonPanel ResolvePanel(UIControlledApplication application)
        {
            foreach (var panel in application.GetRibbonPanels(Tab.AddIns))
            {
                if (panel.Name == PanelName) return panel;
            }

            return application.CreateRibbonPanel(Tab.AddIns, PanelName);
        }
    }
}
