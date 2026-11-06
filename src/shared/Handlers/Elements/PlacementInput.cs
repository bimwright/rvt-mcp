using System;
using System.Linq;

namespace RvtMcp.Plugin.Handlers
{
    // API-free validation shared by the typed placement handlers and unit tests.
    public static class PlacementInput
    {
        public static long[] ElementIds(long[] ids)
        {
            if (ids == null || ids.Length == 0 || ids.Any(id => id <= 0))
                throw new ArgumentException("elementIds must contain positive element ids.");
            if (ids.Length > 1000) throw new ArgumentException("At most 1000 elements per operation.");
            return ids.Distinct().ToArray();
        }

        public static void Finite(params double[] values)
        {
            if (values.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("Coordinates and angles must be finite numbers.");
        }

    }
}
