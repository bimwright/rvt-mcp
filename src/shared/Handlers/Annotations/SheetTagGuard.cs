using System.Linq;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Model elements are tagged in the view placed on a sheet, not on the sheet itself,
    /// so tag commands reject a sheet and name the views the caller can use instead.
    /// </summary>
    internal static class SheetTagGuard
    {
        private const int MaxListedViews = 10;

        public static string Error(Document doc, ViewSheet sheet)
        {
            var placed = sheet.GetAllPlacedViews()
                .Select(id => doc.GetElement(id) as View)
                .Where(v => v != null)
                .OrderBy(v => RevitCompat.GetId(v.Id))
                .ToList();
            var message = "Tags cannot be placed on sheet '" + sheet.SheetNumber + "'. Tag in a view placed on it";
            if (placed.Count == 0)
                return message + "; this sheet has no placed views.";

            var listed = string.Join(", ", placed.Take(MaxListedViews)
                .Select(v => RevitCompat.GetId(v.Id) + " '" + v.Name + "'"));
            var more = placed.Count > MaxListedViews
                ? " and " + (placed.Count - MaxListedViews) + " more (revit_analyze_sheet_layout lists them all)"
                : "";
            return message + ", by view_id: " + listed + more + ".";
        }
    }
}
