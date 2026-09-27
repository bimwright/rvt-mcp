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

        /// <summary>Name plus the full description, including text the cell ellipsizes.</summary>
        public string ToolTipText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name)) return Description ?? string.Empty;
                if (string.IsNullOrWhiteSpace(Description)) return Name;
                return Name + "\n" + Description;
            }
        }

        public bool MatchesQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            var term = query.Trim();
            return Contains(Name, term) || Contains(Description, term) || Contains(DescriptionDisplay, term);
        }

        private static bool Contains(string value, string term)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
