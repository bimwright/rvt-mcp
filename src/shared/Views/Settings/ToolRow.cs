using System;
using RvtMcp.Plugin.Localization;
using RvtMcp.ToolCatalog;

namespace RvtMcp.Plugin.Views.Settings
{
    public sealed class ToolRow
    {
        public int No { get; internal set; }
        public string Name { get; internal set; }
        public string Description { get; internal set; }
        public string DescriptionDisplay
        {
            get
            {
                if (!IsSummaryFallback) return Description;
                var marker = SettingsText.Text("settings.tools.fallback", "(fallback)");
                var suffix = " " + marker;
                if (ToolSummaryCatalog.ScalarLength(Description) + ToolSummaryCatalog.ScalarLength(suffix) <= 160)
                    return Description + suffix;
                var limit = Math.Max(0, 160 - ToolSummaryCatalog.ScalarLength(suffix));
                return ToolCatalogCodec.TakeScalars(Description, limit).TrimEnd() + suffix;
            }
        }
        public bool IsSummaryFallback { get; internal set; }
        public string Source { get; internal set; }
        public string TimeoutDisplay { get; internal set; }
        public string TimeoutHelpText { get; internal set; }
    }
}
