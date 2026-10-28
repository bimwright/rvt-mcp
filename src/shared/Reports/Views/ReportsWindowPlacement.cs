using System;
using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin.Reports.Views
{
    /// <summary>Pure placement geometry for the Reports window, in DIPs; no WPF or screen access.</summary>
    public static class ReportsWindowPlacement
    {
        public const double WideWidth = 790, WideHeight = 540, WideMinWidth = 720, WideMinHeight = 460;
        public const double CompactWidth = 390, CompactHeight = 610, CompactMinWidth = 360, CompactMinHeight = 460;

        /// <summary>Compact stays a narrow column; past this width it is Wide's job.</summary>
        public const double CompactMaxWidth = 560;

        /// <summary>Gap between a default compact window and the owner's right edge.</summary>
        public const double CompactMargin = 16;

        /// <summary>A default compact window is this much shorter than the owner, when the owner is short.</summary>
        public const double CompactOwnerInset = 32;

        public static double MinWidth(ReportsWindowMode mode) => mode == ReportsWindowMode.Compact ? CompactMinWidth : WideMinWidth;
        public static double MinHeight(ReportsWindowMode mode) => mode == ReportsWindowMode.Compact ? CompactMinHeight : WideMinHeight;
        public static double MaxWidth(ReportsWindowMode mode) => mode == ReportsWindowMode.Compact ? CompactMaxWidth : double.PositiveInfinity;

        /// <summary>
        /// Wide is centred on the owner; compact hugs the owner's right edge, centred vertically.
        /// Without a usable owner the work area stands in for it. The result is clamped into <paramref name="work"/>.
        /// </summary>
        public static ReportsWindowBounds Default(ReportsWindowMode mode, ToastBounds? owner, ToastBounds work)
        {
            var o = Usable(owner) ? owner.Value : work;
            if (mode == ReportsWindowMode.Compact)
            {
                var height = Math.Max(CompactMinHeight, Math.Min(CompactHeight, o.Height - CompactOwnerInset));
                return Fit(mode, o.Right - CompactMargin - CompactWidth, o.Top + (o.Height - height) / 2, CompactWidth, height, work);
            }
            return Fit(mode, o.Left + (o.Width - WideWidth) / 2, o.Top + (o.Height - WideHeight) / 2, WideWidth, WideHeight, work);
        }

        /// <summary>Saved bounds kept within [minimum, work area] and clamped into it; missing or invalid ones use <see cref="Default"/>.</summary>
        public static ReportsWindowBounds Restore(ReportsWindowMode mode, ReportsWindowBounds saved, ToastBounds? owner, ToastBounds work)
        {
            if (saved == null || !saved.IsValid) return Default(mode, owner, work);
            return Fit(mode, saved.Left, saved.Top, saved.Width, saved.Height, work);
        }

        private static ReportsWindowBounds Fit(ReportsWindowMode mode, double left, double top, double width, double height, ToastBounds work)
        {
            width = Math.Max(MinWidth(mode), Math.Min(width, Math.Min(MaxWidth(mode), work.Width)));
            height = Math.Max(MinHeight(mode), Math.Min(height, work.Height));
            var clamped = ToastPlacement.Clamp(new ToastBounds(left, top, width, height), work);
            return new ReportsWindowBounds(clamped.Left, clamped.Top, clamped.Width, clamped.Height);
        }

        private static bool Usable(ToastBounds? owner) => owner.HasValue
            && Finite(owner.Value.Left) && Finite(owner.Value.Top) && Finite(owner.Value.Width) && Finite(owner.Value.Height)
            && owner.Value.Width > 0 && owner.Value.Height > 0;

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
