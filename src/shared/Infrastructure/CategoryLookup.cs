using System;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Resolves a category from its display name (case-insensitive).
    /// Top-level categories are matched first. A subcategory can share a top-level name, such as
    /// the "Lines" line style inside the Lines category (#77). Only names that no top-level
    /// category has fall back to a scan of every BuiltInCategory.
    /// </summary>
    internal static class CategoryLookup
    {
        public static Category FindByName(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat != null && string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase))
                    return cat;
            }

            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Category.GetCategory(doc, bic);
                    if (cat != null && string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase))
                        return cat;
                }
                catch { }
            }

            return null;
        }

        public static BuiltInCategory ToBuiltIn(Category cat) => (BuiltInCategory)(int)RevitCompat.GetId(cat.Id);
    }
}
