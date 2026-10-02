using System;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>Pure DIP rectangle; no WPF, host objects, or screen-coordinate persistence.</summary>
    public readonly struct ToastBounds
    {
        public ToastBounds(double left, double top, double width, double height)
        {
            Left = left; Top = top; Width = Math.Max(0, width); Height = Math.Max(0, height);
        }

        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    /// <summary>Pure placement geometry for the activity card.</summary>
    public static class ToastPlacement
    {
        public const double Margin = 16;

        /// <summary>Corner anchor inside the owner window, plus the saved drag offset when drag is on.</summary>
        public static ToastBounds Anchor(ToastBounds owner, double width, double height, ToastPositionOptions options)
        {
            var left = options.Right ? owner.Right - Margin - width : owner.Left + Margin;
            var top = options.Bottom ? owner.Bottom - Margin - height : owner.Top + Margin;
            if (options.DragEnabled && options.HasOffset)
            {
                left += options.OffsetX.Value;
                top += options.OffsetY.Value;
            }
            return new ToastBounds(left, top, width, height);
        }

        /// <summary>Keep the card inside the work area; non-finite input falls back to the work-area corner.</summary>
        public static ToastBounds Clamp(ToastBounds card, ToastBounds work)
        {
            var left = ToastPositionOptions.Finite(card.Left) ? card.Left : work.Left;
            var top = ToastPositionOptions.Finite(card.Top) ? card.Top : work.Top;
            return new ToastBounds(Math.Max(work.Left, Math.Min(left, work.Right - card.Width)),
                Math.Max(work.Top, Math.Min(top, work.Bottom - card.Height)), card.Width, card.Height);
        }

        /// <summary>True when extra height should open upward: preferred if there is room or more room above.</summary>
        public static bool GrowUp(ToastBounds card, ToastBounds work, double extraHeight, bool preferUp)
        {
            var above = card.Top - work.Top;
            var below = work.Bottom - card.Bottom;
            return preferUp ? above >= extraHeight || above >= below : below < extraHeight && above > below;
        }

        /// <summary>A press only becomes a drag past the system drag distance; below it stays a click.</summary>
        public static bool DragThreshold(double dx, double dy, double horizontal, double vertical) =>
            Math.Abs(dx) >= horizontal || Math.Abs(dy) >= vertical;
    }
}
