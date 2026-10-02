using System;

namespace RvtMcp.Plugin
{
    /// <summary>Immutable, host-relative DIP preferences shared across Revit years.</summary>
    public sealed class ToastPositionOptions
    {
        public ToastPositionOptions(bool right = false, bool bottom = false, bool dragEnabled = false,
            double? offsetX = null, double? offsetY = null)
        {
            Right = right;
            Bottom = bottom;
            DragEnabled = dragEnabled;
            if (offsetX.HasValue && offsetY.HasValue && Finite(offsetX.Value) && Finite(offsetY.Value))
            {
                OffsetX = offsetX;
                OffsetY = offsetY;
            }
        }

        public bool Right { get; }
        public bool Bottom { get; }
        public bool DragEnabled { get; }
        public double? OffsetX { get; }
        public double? OffsetY { get; }
        public bool HasOffset => OffsetX.HasValue;

        /// <summary>A drag offset is only meaningful for the corner it was measured from.</summary>
        public ToastPositionOptions WithCorner(bool right, bool bottom) => new ToastPositionOptions(right, bottom, DragEnabled,
            right == Right && bottom == Bottom ? OffsetX : null,
            right == Right && bottom == Bottom ? OffsetY : null);

        /// <summary>Turning drag off keeps the saved offset so turning it on restores it.</summary>
        public ToastPositionOptions WithDrag(bool enabled) => new ToastPositionOptions(Right, Bottom, enabled, OffsetX, OffsetY);

        public ToastPositionOptions WithOffset(double? x, double? y) => new ToastPositionOptions(Right, Bottom, DragEnabled, x, y);

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
