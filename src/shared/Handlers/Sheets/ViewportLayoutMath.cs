using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Axis-aligned rectangle in sheet space. Unit-agnostic: callers pass feet or millimetres
    /// consistently and read the same unit back.
    /// </summary>
    public readonly struct SheetRect
    {
        public SheetRect(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public double CenterX => (MinX + MaxX) * 0.5;
        public double CenterY => (MinY + MaxY) * 0.5;

        public SheetRect Translated(double dx, double dy) => new SheetRect(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);
        public SheetRect Scaled(double factor) => new SheetRect(MinX * factor, MinY * factor, MaxX * factor, MaxY * factor);
    }

    /// <summary>How far one rectangle extends past another on each side; positive means outward.</summary>
    public readonly struct SheetMargins
    {
        public SheetMargins(double left, double right, double top, double bottom)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }

        public double Left { get; }
        public double Right { get; }
        public double Top { get; }
        public double Bottom { get; }

        public double Max => Math.Max(Math.Max(Left, Right), Math.Max(Top, Bottom));
    }

    public enum ViewportAlignMode
    {
        Left,
        Right,
        Top,
        Bottom,
        CenterX,
        CenterY,
        Center,
        CenterOnSheet
    }

    /// <summary>
    /// Sheet-space arithmetic for viewports, free of Revit API references so the test assembly can
    /// compile this file. A view's model-space crop box and its paper outline live in one paper frame
    /// (crop box divided by view scale); the viewport box centre anchors that frame onto the sheet.
    /// All members work in a single unit chosen by the caller.
    /// </summary>
    public static class ViewportLayoutMath
    {
        /// <summary>Offset that moves the view's paper frame onto the sheet.</summary>
        public static (double X, double Y) PaperToSheetOffset(
            double boxCenterX, double boxCenterY,
            double outlineMinU, double outlineMinV, double outlineMaxU, double outlineMaxV)
        {
            return (
                boxCenterX - (outlineMinU + outlineMaxU) * 0.5,
                boxCenterY - (outlineMinV + outlineMaxV) * 0.5);
        }

        /// <summary>The view's paper outline (includes annotation that extends past the crop) on the sheet.</summary>
        public static SheetRect OutlineOnSheet(
            double boxCenterX, double boxCenterY,
            double outlineMinU, double outlineMinV, double outlineMaxU, double outlineMaxV)
        {
            var offset = PaperToSheetOffset(boxCenterX, boxCenterY, outlineMinU, outlineMinV, outlineMaxU, outlineMaxV);
            return new SheetRect(
                outlineMinU + offset.X, outlineMinV + offset.Y,
                outlineMaxU + offset.X, outlineMaxV + offset.Y);
        }

        /// <summary>The visible crop rectangle on the sheet: crop box divided by view scale, then anchored.</summary>
        public static SheetRect CropRegionOnSheet(
            double boxCenterX, double boxCenterY,
            double outlineMinU, double outlineMinV, double outlineMaxU, double outlineMaxV,
            double cropMinX, double cropMinY, double cropMaxX, double cropMaxY,
            double viewScale)
        {
            if (viewScale <= 0) throw new ArgumentOutOfRangeException(nameof(viewScale), "View scale must be positive.");
            var offset = PaperToSheetOffset(boxCenterX, boxCenterY, outlineMinU, outlineMinV, outlineMaxU, outlineMaxV);
            return new SheetRect(
                cropMinX / viewScale + offset.X, cropMinY / viewScale + offset.Y,
                cropMaxX / viewScale + offset.X, cropMaxY / viewScale + offset.Y);
        }

        /// <summary>How far the outline extends past the crop on each side (annotation overflow).</summary>
        public static SheetMargins OverflowBeyond(SheetRect outline, SheetRect crop)
        {
            return new SheetMargins(
                crop.MinX - outline.MinX,
                outline.MaxX - crop.MaxX,
                outline.MaxY - crop.MaxY,
                crop.MinY - outline.MinY);
        }

        public static SheetRect Union(IEnumerable<SheetRect> rects)
        {
            var list = rects?.ToList();
            if (list == null || list.Count == 0) throw new ArgumentException("At least one rectangle is required.", nameof(rects));
            return new SheetRect(
                list.Min(r => r.MinX), list.Min(r => r.MinY),
                list.Max(r => r.MaxX), list.Max(r => r.MaxY));
        }

        public static bool TryParseMode(string text, out ViewportAlignMode mode)
        {
            mode = ViewportAlignMode.Left;
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "left": mode = ViewportAlignMode.Left; return true;
                case "right": mode = ViewportAlignMode.Right; return true;
                case "top": mode = ViewportAlignMode.Top; return true;
                case "bottom": mode = ViewportAlignMode.Bottom; return true;
                case "center_x": mode = ViewportAlignMode.CenterX; return true;
                case "center_y": mode = ViewportAlignMode.CenterY; return true;
                case "center": mode = ViewportAlignMode.Center; return true;
                case "center_on_sheet": mode = ViewportAlignMode.CenterOnSheet; return true;
                default: return false;
            }
        }

        public static bool NeedsReference(ViewportAlignMode mode) => mode != ViewportAlignMode.CenterOnSheet;

        /// <summary>Translation that brings <paramref name="target"/> onto <paramref name="reference"/> along the mode's axis.</summary>
        public static (double Dx, double Dy) AlignDelta(SheetRect reference, SheetRect target, ViewportAlignMode mode)
        {
            switch (mode)
            {
                case ViewportAlignMode.Left: return (reference.MinX - target.MinX, 0.0);
                case ViewportAlignMode.Right: return (reference.MaxX - target.MaxX, 0.0);
                case ViewportAlignMode.Top: return (0.0, reference.MaxY - target.MaxY);
                case ViewportAlignMode.Bottom: return (0.0, reference.MinY - target.MinY);
                case ViewportAlignMode.CenterX: return (reference.CenterX - target.CenterX, 0.0);
                case ViewportAlignMode.CenterY: return (0.0, reference.CenterY - target.CenterY);
                case ViewportAlignMode.Center:
                    return (reference.CenterX - target.CenterX, reference.CenterY - target.CenterY);
                default:
                    throw new ArgumentException("Mode '" + mode + "' does not align to a reference rectangle.", nameof(mode));
            }
        }

        /// <summary>One translation, shared by every member, that centres the group's bounding box on the frame.</summary>
        public static (double Dx, double Dy) CenterGroupOnFrame(SheetRect frame, SheetRect group)
        {
            return (frame.CenterX - group.CenterX, frame.CenterY - group.CenterY);
        }

        /// <summary>
        /// What Revit kept after a move was committed: every planned viewport at its target, only some,
        /// or none. Revit can undo a move at regeneration (for example a saved position), so the
        /// commit succeeding does not mean the viewport moved.
        /// </summary>
        public static string ClassifyApply(int plannedCount, int heldCount)
        {
            if (plannedCount < 0 || heldCount < 0 || heldCount > plannedCount)
                throw new ArgumentOutOfRangeException(nameof(heldCount), "heldCount must be between 0 and plannedCount.");
            if (heldCount == plannedCount) return "applied";
            return heldCount == 0 ? "not_applied" : "partially_applied";
        }
    }
}
