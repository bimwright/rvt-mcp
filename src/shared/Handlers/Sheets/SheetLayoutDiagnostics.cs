using System;
using System.Collections.Generic;

namespace RvtMcp.Plugin.Handlers
{
    public sealed class SheetViewportBox
    {
        public long VpId { get; set; }
        public long ViewId { get; set; }
        public string ViewName { get; set; }
        public SheetRect BoxMm { get; set; }
    }

    public sealed class SheetBoxOverlap
    {
        public long VpIdA { get; set; }
        public long ViewIdA { get; set; }
        public string ViewNameA { get; set; }
        public long VpIdB { get; set; }
        public long ViewIdB { get; set; }
        public string ViewNameB { get; set; }
        public double OverlapWidthMm { get; set; }
        public double OverlapHeightMm { get; set; }
    }

    public sealed class SheetOutsideTitleBlock
    {
        public long VpId { get; set; }
        public long ViewId { get; set; }
        public string ViewName { get; set; }
        public double OverflowLeftMm { get; set; }
        public double OverflowRightMm { get; set; }
        public double OverflowTopMm { get; set; }
        public double OverflowBottomMm { get; set; }
        public double MaxOverflowMm { get; set; }
    }

    public sealed class SheetLayoutDiagnosticsResult
    {
        public List<SheetBoxOverlap> BoxOverlaps { get; set; } = new List<SheetBoxOverlap>();
        public List<SheetOutsideTitleBlock> OutsideTitleBlock { get; set; } = new List<SheetOutsideTitleBlock>();
    }

    /// <summary>
    /// Host-free viewport-box overlap and title-block bounds checks for <c>analyze_sheet_layout</c>.
    /// Operates on Revit viewport boxes (paper outlines, not tight crop polygons) in millimetres.
    /// </summary>
    public static class SheetLayoutDiagnostics
    {
        public const double DefaultToleranceMm = 1.0;

        public static SheetLayoutDiagnosticsResult Analyze(
            SheetRect? titleBlockMm,
            IReadOnlyList<SheetViewportBox> viewports,
            double toleranceMm = DefaultToleranceMm)
        {
            if (viewports == null)
                throw new ArgumentNullException(nameof(viewports));
            if (double.IsNaN(toleranceMm) || double.IsInfinity(toleranceMm) || toleranceMm < 0)
                throw new ArgumentOutOfRangeException(nameof(toleranceMm), "toleranceMm must be a finite non-negative number.");

            var result = new SheetLayoutDiagnosticsResult();
            if (viewports.Count == 0)
                return result;

            for (var i = 0; i < viewports.Count; i++)
            {
                var a = viewports[i];

                if (titleBlockMm.HasValue)
                {
                    var overflow = ViewportLayoutMath.OverflowBeyond(a.BoxMm, titleBlockMm.Value);
                    var left = overflow.Left > toleranceMm ? Math.Round(overflow.Left, 2) : 0.0;
                    var right = overflow.Right > toleranceMm ? Math.Round(overflow.Right, 2) : 0.0;
                    var top = overflow.Top > toleranceMm ? Math.Round(overflow.Top, 2) : 0.0;
                    var bottom = overflow.Bottom > toleranceMm ? Math.Round(overflow.Bottom, 2) : 0.0;
                    var max = Math.Max(Math.Max(left, right), Math.Max(top, bottom));

                    if (max > 0.0)
                    {
                        result.OutsideTitleBlock.Add(new SheetOutsideTitleBlock
                        {
                            VpId = a.VpId,
                            ViewId = a.ViewId,
                            ViewName = a.ViewName,
                            OverflowLeftMm = left,
                            OverflowRightMm = right,
                            OverflowTopMm = top,
                            OverflowBottomMm = bottom,
                            MaxOverflowMm = max
                        });
                    }
                }

                for (var j = i + 1; j < viewports.Count; j++)
                {
                    var b = viewports[j];

                    var overlapW = Math.Min(a.BoxMm.MaxX, b.BoxMm.MaxX) - Math.Max(a.BoxMm.MinX, b.BoxMm.MinX);
                    var overlapH = Math.Min(a.BoxMm.MaxY, b.BoxMm.MaxY) - Math.Max(a.BoxMm.MinY, b.BoxMm.MinY);
                    if (overlapW > toleranceMm && overlapH > toleranceMm)
                    {
                        var roundedW = Math.Round(overlapW, 2);
                        var roundedH = Math.Round(overlapH, 2);
                        if (roundedW > 0.0 && roundedH > 0.0)
                        {
                            result.BoxOverlaps.Add(new SheetBoxOverlap
                            {
                                VpIdA = a.VpId,
                                ViewIdA = a.ViewId,
                                ViewNameA = a.ViewName,
                                VpIdB = b.VpId,
                                ViewIdB = b.ViewId,
                                ViewNameB = b.ViewName,
                                OverlapWidthMm = roundedW,
                                OverlapHeightMm = roundedH
                            });
                        }
                    }
                }
            }

            return result;
        }
    }
}
