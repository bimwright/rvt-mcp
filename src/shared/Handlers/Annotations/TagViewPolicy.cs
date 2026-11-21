using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Host-free rule for tag tools: a sheet holds viewports, not model elements, so tags
    /// belong in the model view placed on it. Text notes on sheets are not covered here.
    /// </summary>
    public static class TagViewPolicy
    {
        public const string SheetViewType = "DrawingSheet";

        // Views that show model elements and can hold their tags. A rendering (an image), a legend,
        // a schedule or a drafting view placed on a sheet cannot.
        private static readonly HashSet<string> TaggableViewTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "FloorPlan", "CeilingPlan", "EngineeringPlan", "AreaPlan", "Section", "Elevation", "Detail", "ThreeD",
        };

        /// <summary>True when a view of this ViewType name can hold tags of model elements.</summary>
        public static bool IsTaggableViewType(string viewType) => viewType != null && TaggableViewTypes.Contains(viewType);

        /// <summary>
        /// Returns the refusal for a sheet view, or null when the view type may be tagged. A tool that
        /// tags the active view (it takes no viewId) tells the caller to activate a placed view instead.
        /// </summary>
        public static string SheetRefusal(string viewType, string sheetName, IEnumerable<KeyValuePair<long, string>> placedViews, bool usesActiveView = false)
        {
            if (!string.Equals(viewType, SheetViewType, StringComparison.Ordinal))
                return null;

            var views = (placedViews ?? Enumerable.Empty<KeyValuePair<long, string>>())
                .Select(v => v.Value + " (viewId " + v.Key + ")")
                .ToList();
            var placed = views.Count == 0
                ? " No model view is placed on it."
                : " Views placed on it: " + string.Join(", ", views) + ".";
            var next = usesActiveView
                ? "this tool tags the active view, so activate a view placed on it (revit_activate_view) and call again."
                : "pass the viewId of the view placed on it.";
            return "View '" + sheetName + "' is a sheet. Tags go in a model view, not on the sheet; " + next + placed;
        }
    }
}
