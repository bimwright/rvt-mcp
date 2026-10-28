using System;
using System.Collections.Generic;

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

    /// <summary>Text size of the Reports content area, in percent; the window offers only these steps.</summary>
    public static class ReportsTextScale
    {
        public const int Default = 100;
        public static readonly IReadOnlyList<int> Steps = new[] { 90, 100, 110, 125, 150 };

        /// <summary>A value that is not one of the steps (hand-edited config, older build) reads as 100 %.</summary>
        public static int Normalize(int percent) => Index(percent) >= 0 ? percent : Default;

        public static int Larger(int percent) => Steps[Math.Min(Index(Normalize(percent)) + 1, Steps.Count - 1)];
        public static int Smaller(int percent) => Steps[Math.Max(Index(Normalize(percent)) - 1, 0)];

        private static int Index(int percent)
        {
            for (var i = 0; i < Steps.Count; i++)
                if (Steps[i] == percent) return i;
            return -1;
        }
    }

    /// <summary>
    /// Immutable Reports window preference: the last display mode, the bounds each mode last used and the text size.
    /// Invalid bounds are dropped, so a missing mode means "use the default placement".
    /// </summary>
    public sealed class ReportsWindowState
    {
        public ReportsWindowState(ReportsWindowMode mode = ReportsWindowMode.Wide,
            ReportsWindowBounds wide = null, ReportsWindowBounds compact = null, int textScale = ReportsTextScale.Default)
        {
            Mode = mode == ReportsWindowMode.Compact ? ReportsWindowMode.Compact : ReportsWindowMode.Wide;
            Wide = wide != null && wide.IsValid ? wide : null;
            Compact = compact != null && compact.IsValid ? compact : null;
            TextScale = ReportsTextScale.Normalize(textScale);
        }

        public ReportsWindowMode Mode { get; }
        public ReportsWindowBounds Wide { get; }
        public ReportsWindowBounds Compact { get; }
        public int TextScale { get; }

        public ReportsWindowBounds BoundsFor(ReportsWindowMode mode) => mode == ReportsWindowMode.Compact ? Compact : Wide;

        public ReportsWindowState WithMode(ReportsWindowMode mode) => new ReportsWindowState(mode, Wide, Compact, TextScale);

        public ReportsWindowState WithBounds(ReportsWindowMode mode, ReportsWindowBounds bounds) =>
            mode == ReportsWindowMode.Compact
                ? new ReportsWindowState(Mode, Wide, bounds, TextScale)
                : new ReportsWindowState(Mode, bounds, Compact, TextScale);

        public ReportsWindowState WithTextScale(int percent) => new ReportsWindowState(Mode, Wide, Compact, percent);
    }
}
