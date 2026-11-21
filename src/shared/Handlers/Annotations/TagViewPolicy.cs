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

        /// <summary>Returns the refusal for a sheet view, or null when the view type may be tagged.</summary>
        public static string SheetRefusal(string viewType, string sheetName, IEnumerable<KeyValuePair<long, string>> placedViews)
        {
            if (!string.Equals(viewType, SheetViewType, StringComparison.Ordinal))
                return null;

            var views = (placedViews ?? Enumerable.Empty<KeyValuePair<long, string>>())
                .Select(v => v.Value + " (viewId " + v.Key + ")")
                .ToList();
            var placed = views.Count == 0
                ? " No model view is placed on it."
                : " Views placed on it: " + string.Join(", ", views) + ".";
            return "View '" + sheetName + "' is a sheet. Tags go in a model view, not on the sheet; pass the viewId of the view placed on it." + placed;
        }
    }
}
