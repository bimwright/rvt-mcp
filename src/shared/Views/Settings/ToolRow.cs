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
                var description = SettingsText.AsciiDashes(Description);
                if (!IsSummaryFallback) return description;
                var marker = SettingsText.Text("settings.tools.fallback", "(fallback)");
                var suffix = " " + marker;
                if (ToolSummaryCatalog.ScalarLength(description) + ToolSummaryCatalog.ScalarLength(suffix) <= 160)
                    return description + suffix;
                var limit = Math.Max(0, 160 - ToolSummaryCatalog.ScalarLength(suffix));
                return ToolCatalogCodec.TakeScalars(description, limit).TrimEnd() + suffix;
            }
        }
        public bool IsSummaryFallback { get; internal set; }
        public string Source { get; internal set; }
        private string _timeoutDisplay, _timeoutHelpText;
        public string TimeoutDisplay { get => SettingsText.AsciiDashes(_timeoutDisplay); internal set => _timeoutDisplay = value; }
        public string TimeoutHelpText { get => SettingsText.AsciiDashes(_timeoutHelpText); internal set => _timeoutHelpText = value; }

        /// <summary>Name plus the full description, including text the cell ellipsizes.</summary>
        public string ToolTipText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name)) return SettingsText.AsciiDashes(Description ?? string.Empty);
                if (string.IsNullOrWhiteSpace(Description)) return Name;
                return Name + "\n" + SettingsText.AsciiDashes(Description);
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
