using System;
using System.Reflection;

namespace RvtMcp.Plugin.Views.Settings
{
    public sealed partial class SettingsWindow
    {
        // Host wiring lives outside the presentation so the real UI can be exercised without Autodesk.
        public SettingsWindow(App app, SettingsTab initialTab = SettingsTab.General)
            : this(new SettingsViewModel(new Infrastructure.UI.SettingsRuntime(app ?? throw new ArgumentNullException(nameof(app)))),
                new ToolsViewModel(new Infrastructure.UI.ToolsCatalogSource(app.BakedToolRegistry, Assembly.GetExecutingAssembly())), initialTab)
        {
        }
    }
}
