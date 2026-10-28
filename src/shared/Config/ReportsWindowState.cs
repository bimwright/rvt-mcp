using System;

namespace RvtMcp.Plugin
{
    public enum ReportsWindowMode { Wide, Compact }

    /// <summary>Saved Reports window rectangle in WPF DIPs (<c>Window.Left/Top/Width/Height</c>).</summary>
    public sealed class ReportsWindowBounds
    {
        public const double MaxSize = 20000;
        public const double MaxOffset = 100000;

        public ReportsWindowBounds(double left, double top, double width, double height)
        {
            Left = left; Top = top; Width = width; Height = height;
        }

        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }

        /// <summary>Four finite numbers, <c>0 &lt; size ≤ 20000</c> and <c>|offset| ≤ 100000</c>.</summary>
        public bool IsValid => Offset(Left) && Offset(Top) && Size(Width) && Size(Height);

        private static bool Offset(double value) => Finite(value) && Math.Abs(value) <= MaxOffset;
        private static bool Size(double value) => Finite(value) && value > 0 && value <= MaxSize;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// Immutable Reports window preference: the last display mode and the bounds each mode last used.
    /// Invalid bounds are dropped, so a missing mode means "use the default placement".
    /// </summary>
    public sealed class ReportsWindowState
    {
        public ReportsWindowState(ReportsWindowMode mode = ReportsWindowMode.Wide,
            ReportsWindowBounds wide = null, ReportsWindowBounds compact = null)
        {
            Mode = mode == ReportsWindowMode.Compact ? ReportsWindowMode.Compact : ReportsWindowMode.Wide;
            Wide = wide != null && wide.IsValid ? wide : null;
            Compact = compact != null && compact.IsValid ? compact : null;
        }

        public ReportsWindowMode Mode { get; }
        public ReportsWindowBounds Wide { get; }
        public ReportsWindowBounds Compact { get; }

        public ReportsWindowBounds BoundsFor(ReportsWindowMode mode) => mode == ReportsWindowMode.Compact ? Compact : Wide;

        public ReportsWindowState WithMode(ReportsWindowMode mode) => new ReportsWindowState(mode, Wide, Compact);

        public ReportsWindowState WithBounds(ReportsWindowMode mode, ReportsWindowBounds bounds) =>
            mode == ReportsWindowMode.Compact
                ? new ReportsWindowState(Mode, Wide, bounds)
                : new ReportsWindowState(Mode, bounds, Compact);
    }
}
