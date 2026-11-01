using System;
using System.Collections;
using System.Reflection;

namespace RvtMcp.Plugin.Ribbon
{
    /// <summary>Optional adapter to the host ribbon implementation, without a build-time UI-framework dependency.</summary>
    internal static class RibbonIconOnly
    {
        internal static object Apply(object ribbon, string panelTitle, string command)
        {
            foreach (var tab in Items(ribbon, "Tabs"))
                foreach (var panel in Items(tab, "Panels"))
                {
                    var source = Value(panel, "Source");
                    if (!string.Equals(Value(source, "Title") as string, panelTitle, StringComparison.Ordinal)) continue;
                    var item = Find(source, command, 0);
                    if (item == null) continue;
                    var show = item.GetType().GetProperty("ShowText");
                    var size = item.GetType().GetProperty("Size");
                    if (show?.CanWrite != true || show.PropertyType != typeof(bool) || size?.CanWrite != true || !size.PropertyType.IsEnum) return null;
                    var oldShow = show.GetValue(item, null);
                    var oldSize = size.GetValue(item, null);
                    try
                    {
                        var large = Enum.Parse(size.PropertyType, "Large");
                        size.SetValue(item, large, null);
                        show.SetValue(item, false, null);
                        return item;
                    }
                    catch
                    {
                        try { show.SetValue(item, oldShow, null); size.SetValue(item, oldSize, null); } catch { }
                        return null;
                    }
                }
            return null;
        }

        private static object Find(object parent, string command, int depth)
        {
            if (depth > 8) return null;
            foreach (var item in Items(parent, "Items"))
            {
                var id = Value(item, "Id") as string ?? Value(item, "Name") as string;
                if (id == command || id?.EndsWith("%" + command, StringComparison.Ordinal) == true) return item;
                var nested = Find(item, command, depth + 1);
                if (nested != null) return nested;
            }
            return null;
        }
        private static IEnumerable Items(object owner, string name) => Value(owner, name) as IEnumerable ?? new object[0];
        private static object Value(object owner, string name) => owner?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(owner, null);
    }
}
