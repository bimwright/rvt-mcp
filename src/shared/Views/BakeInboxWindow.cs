using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.ToolBaker;

namespace RvtMcp.Plugin.Views
{
    public sealed class BakeInboxWindow
    {
        private readonly BakedToolRegistry _registry;
        private readonly BakedToolRuntimeCache _runtimeCache;

        public BakeInboxWindow(BakedToolRegistry registry, BakedToolRuntimeCache runtimeCache)
        {
            _registry = registry;
            _runtimeCache = runtimeCache;
        }

        public bool IsLoaded { get; private set; }

        public void ShowOrFocus()
        {
            IsLoaded = true;
            var dialog = new TaskDialog(L.T("bakeInbox.title"))
            {
                MainInstruction = L.T("bakeInbox.instruction"),
                MainContent = BuildContent(),
                CommonButtons = TaskDialogCommonButtons.Ok
            };
            dialog.Show();
            IsLoaded = false;
        }

        public void Close()
        {
            IsLoaded = false;
        }

        private string BuildContent()
        {
            var tools = _registry?.GetAll()?.OrderBy(t => t.Name).ToArray() ?? new BakedToolMeta[0];
            if (tools.Length == 0)
                return L.T("bakeInbox.empty");

            var sb = new StringBuilder();
            foreach (var tool in tools.Take(24))
            {
                var runtime = _runtimeCache?.GetByName(tool.Name);
                var slot = runtime?.RibbonSlot > 0 ? L.T("bakeInbox.ribbonSlot", ("slot", runtime.RibbonSlot)) : string.Empty;
                sb.AppendLine((tool.DisplayName ?? tool.Name) + slot);
                if (!string.IsNullOrWhiteSpace(tool.Description))
                    sb.AppendLine("  " + tool.Description);
            }

            if (tools.Length > 24)
                sb.AppendLine("...");

            return sb.ToString();
        }
    }
}
