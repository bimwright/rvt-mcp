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
        public PushButton BakeInboxButton { get; set; }
        public ComboBox LanguageCombo { get; set; }
    }

    public static class RibbonSetup
    {
        private const string PanelName = "RvtMcp";
        private static readonly HashSet<string> CreatedButtons = new HashSet<string>();
        /// <summary>
        /// Revit routes every item added after <c>AddSlideOut()</c> into the
        /// slide-out — so ordering is contractual: all main-panel items first,
        /// then AddSlideOut, then the Language combo; baked-tool buttons added
        /// later (startup or mid-session via RefreshBakedRibbonButtons) land in
        /// the slide-out below Language. Spec §5.2 + owner decision.
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
            PushButton bakeInboxButton = null;
            if (config?.EnableAdaptiveBakeOrDefault == true)
                bakeInboxButton = AddBakeInboxButton(panel, assemblyPath);

            panel.AddSlideOut();
            var languageCombo = AddLanguageCombo(panel, config);

            // Baked-tool buttons must come AFTER the slide-out — they intentionally
            // appear below the Language combo (owner decision; there is no API slot
            // that puts them back in the main panel once a slide-out exists).
            if (config?.EnableAdaptiveBakeOrDefault == true)
                AddOrUpdateBakedToolButtons(application, runtimeCache);

            return new RibbonResult
            {
                ToggleButton = stack[0] as PushButton,
                HistoryButton = stack[1] as PushButton,
                ToastButton = stack[2] as PushButton,
                BakeInboxButton = bakeInboxButton,
                LanguageCombo = languageCombo
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

        /// <summary>
        /// Slide-out language picker: "Auto" + the 15 shipped locales shown under
        /// their native names. Selection applies immediately via <see cref="L.SetLanguage"/>
        /// and persists to <c>uiLanguage</c> unless BIMWRIGHT_UI_LANGUAGE is set
        /// (env wins again at next startup). <c>CurrentChanged</c> is suppressed while
        /// populating so startup cannot overwrite the persisted value.
        /// </summary>
        private static ComboBox AddLanguageCombo(RibbonPanel panel, RvtMcpConfig config)
        {
            var comboData = new ComboBoxData("LanguageCombo")
            {
                ToolTip = L.T("ribbon.language.tooltip")
            };

            ComboBox combo;
            try
            {
                combo = panel.AddItem(comboData) as ComboBox;
            }
            catch
            {
                return null;
            }
            if (combo == null) return null;

            combo.ItemText = L.T("ribbon.language.label");
            var mergedCode = LocaleResolver.NormalizeCode(config?.UiLanguage);
            var autoDisplay = L.T("ribbon.language.auto");

            // Populate before wiring CurrentChanged — no handler can fire during setup.
            ComboBoxMember selected = null;
            var autoItem = combo.AddItem(new ComboBoxMemberData(LocaleResolver.Auto, autoDisplay));
            if (mergedCode == LocaleResolver.Auto) selected = autoItem;
            foreach (var locale in LocaleResolver.SupportedLocales)
            {
                var item = combo.AddItem(new ComboBoxMemberData(locale, LocaleResolver.NativeName(locale)));
                if (mergedCode == locale) selected = item;
            }
            if (selected != null) combo.Current = selected;

            combo.CurrentChanged += (s, e) =>
            {
                var current = combo.Current;
                if (current == null) return;
                var code = current.Name;
                L.SetLanguage(code);
                // Persist only when the env var is absent — env wins at next startup anyway.
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RvtMcpConfig.EnvUiLanguage)))
                    RvtMcpConfig.SaveUiLanguage(code);
            };

            return combo;
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
