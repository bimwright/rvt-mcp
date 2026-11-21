namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Host-free check of an optional sheet point in paper millimetres (the convention
    /// place_schedule_on_sheet and analyze_sheet_layout use): both coordinates or neither.
    /// </summary>
    public static class SheetPointInput
    {
        /// <summary>Returns the error for an unusable pair, or null when the pair is absent or valid.</summary>
        public static string Validate(double? xMm, double? yMm)
        {
            if (xMm.HasValue != yMm.HasValue)
                return "Pass both xMm and yMm (sheet paper coordinates in mm), or neither.";
            if (xMm.HasValue && (!IsFinite(xMm.Value) || !IsFinite(yMm.Value)))
                return "xMm and yMm must be finite numbers.";
            return null;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
