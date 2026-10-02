using System;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Sheet-space arithmetic behind revit_get_viewport_geometry and revit_align_viewports. Runs without
    /// Revit because ViewportLayoutMath has no API references. The measured case comes from two 1:125
    /// section viewports on an A2 sheet whose annotation hangs past the top of the first crop.
    /// </summary>
    public class ViewportLayoutMathTests
    {
        private const double Ft = 304.8;
        private const double Tol = 1e-6;

        // First viewport: sheet-space box centre and crop box in view units (feet), converted to mm.
        private static SheetRect MeasuredCropFirst()
        {
            // Outline = crop paper frame extended 6.2079 mm past the top (annotation overflow).
            const double overflowTop = 6.207904354195563;
            var bcX = 302.2018777709274;
            var bcY = 337.2754931167763;
            double cropMinX = -76.37273279408427 * Ft, cropMinY = -16.463923194116777 * Ft;
            double cropMaxX = 72.79541230956447 * Ft, cropMaxY = 16.94009497512287 * Ft;
            double scale = 125.0;
            return ViewportLayoutMath.CropRegionOnSheet(
                bcX, bcY,
                cropMinX / scale, cropMinY / scale, cropMaxX / scale, cropMaxY / scale + overflowTop,
                cropMinX, cropMinY, cropMaxX, cropMaxY, scale);
        }

        private static SheetRect MeasuredCropSecond()
        {
            // Outline = crop paper frame extended 14.9626 mm past the bottom.
            const double overflowBottom = 14.962559893472644;
            var bcX = 376.1492539400709;
            var bcY = 206.80620037490388;
            double cropMinX = -76.3727327940843 * Ft, cropMinY = -18.151145341033857 * Ft;
            double cropMaxX = 72.79541230956444 * Ft, cropMaxY = 22.704042185392378 * Ft;
            double scale = 125.0;
            return ViewportLayoutMath.CropRegionOnSheet(
                bcX, bcY,
                cropMinX / scale, cropMinY / scale - overflowBottom, cropMaxX / scale, cropMaxY / scale,
                cropMinX, cropMinY, cropMaxX, cropMaxY, scale);
        }

        private static void AssertRect(double minX, double minY, double maxX, double maxY, SheetRect actual)
        {
            Assert.Equal(minX, actual.MinX, 5);
            Assert.Equal(minY, actual.MinY, 5);
            Assert.Equal(maxX, actual.MaxX, 5);
            Assert.Equal(maxY, actual.MaxY, 5);
        }

        [Fact]
        public void Crop_region_on_sheet_matches_the_measured_sheet()
        {
            AssertRect(120.33607526055889, 293.4453619877416, 484.06768028129596, 374.8977198916155, MeasuredCropFirst());
            AssertRect(194.28345142970232, 164.47683568942134, 558.0150564504394, 264.0981249538591, MeasuredCropSecond());
        }

        [Fact]
        public void Outline_on_sheet_is_centred_on_the_box_centre_and_keeps_its_size()
        {
            var outline = ViewportLayoutMath.OutlineOnSheet(100.0, 50.0, -2.0, -1.0, 6.0, 3.0);

            Assert.Equal(100.0, outline.CenterX, 9);
            Assert.Equal(50.0, outline.CenterY, 9);
            Assert.Equal(8.0, outline.Width, 9);
            Assert.Equal(4.0, outline.Height, 9);
        }

        [Fact]
        public void Overflow_reports_annotation_past_the_crop_on_each_side()
        {
            var crop = new SheetRect(10, 20, 110, 70);
            var outline = new SheetRect(8, 20, 110, 76);

            var margins = ViewportLayoutMath.OverflowBeyond(outline, crop);

            Assert.Equal(2.0, margins.Left, 9);
            Assert.Equal(0.0, margins.Right, 9);
            Assert.Equal(6.0, margins.Top, 9);
            Assert.Equal(0.0, margins.Bottom, 9);
            Assert.Equal(6.0, margins.Max, 9);
        }

        [Fact]
        public void Crop_region_rejects_a_non_positive_scale()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ViewportLayoutMath.CropRegionOnSheet(0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0));
        }

        [Theory]
        [InlineData(ViewportAlignMode.Left, -20.0, 0.0)]
        [InlineData(ViewportAlignMode.Right, -10.0, 0.0)]
        [InlineData(ViewportAlignMode.Top, 0.0, 5.0)]
        [InlineData(ViewportAlignMode.Bottom, 0.0, 15.0)]
        [InlineData(ViewportAlignMode.CenterX, -15.0, 0.0)]
        [InlineData(ViewportAlignMode.CenterY, 0.0, 10.0)]
        [InlineData(ViewportAlignMode.Center, -15.0, 10.0)]
        public void Align_delta_moves_the_target_onto_the_reference_edge_or_centre(ViewportAlignMode mode, double dx, double dy)
        {
            var reference = new SheetRect(100, 100, 200, 160);
            var target = new SheetRect(120, 85, 210, 155);

            var delta = ViewportLayoutMath.AlignDelta(reference, target, mode);

            Assert.Equal(dx, delta.Dx, 9);
            Assert.Equal(dy, delta.Dy, 9);
        }

        [Fact]
        public void Align_delta_refuses_the_sheet_centre_mode()
        {
            Assert.Throws<ArgumentException>(() =>
                ViewportLayoutMath.AlignDelta(new SheetRect(0, 0, 1, 1), new SheetRect(0, 0, 1, 1), ViewportAlignMode.CenterOnSheet));
        }

        [Fact]
        public void Centre_x_on_the_measured_pair_makes_both_edges_coincide()
        {
            var first = MeasuredCropFirst();
            var second = MeasuredCropSecond();

            var delta = ViewportLayoutMath.AlignDelta(first, second, ViewportAlignMode.CenterX);
            var moved = second.Translated(delta.Dx, delta.Dy);

            Assert.Equal(first.MinX, moved.MinX, 6);
            Assert.Equal(first.MaxX, moved.MaxX, 6);
            Assert.Equal(0.0, delta.Dy, 9);
            Assert.Equal(second.Height, moved.Height, 9);
        }

        [Fact]
        public void Centring_the_measured_pair_on_the_sheet_frame_keeps_their_gap()
        {
            var first = MeasuredCropFirst();
            var second = MeasuredCropSecond();
            var frame = new SheetRect(0, 0, 594, 420);
            var gapBefore = first.MinY - second.MaxY;

            // Align the second onto the first, then centre the pair as one group.
            var align = ViewportLayoutMath.AlignDelta(first, second, ViewportAlignMode.CenterX);
            var secondAligned = second.Translated(align.Dx, align.Dy);
            var group = ViewportLayoutMath.Union(new[] { first, secondAligned });
            var shift = ViewportLayoutMath.CenterGroupOnFrame(frame, group);

            var firstFinal = first.Translated(shift.Dx, shift.Dy);
            var secondFinal = secondAligned.Translated(shift.Dx, shift.Dy);

            AssertRect(115.13419748963149, 233.75808419722313, 478.86580251036855, 315.21044210109704, firstFinal);
            AssertRect(115.13419748963149, 104.78955789890293, 478.86580251036855, 204.41084716334066, secondFinal);
            Assert.Equal(gapBefore, firstFinal.MinY - secondFinal.MaxY, 6);
            var finalGroup = ViewportLayoutMath.Union(new[] { firstFinal, secondFinal });
            Assert.Equal(297.0, finalGroup.CenterX, 6);
            Assert.Equal(210.0, finalGroup.CenterY, 6);
        }

        [Fact]
        public void Union_covers_every_rectangle_and_rejects_an_empty_list()
        {
            var union = ViewportLayoutMath.Union(new[] { new SheetRect(0, 0, 10, 10), new SheetRect(5, -3, 20, 8) });

            AssertRect(0, -3, 20, 10, union);
            Assert.Throws<ArgumentException>(() => ViewportLayoutMath.Union(Array.Empty<SheetRect>()));
        }

        [Theory]
        [InlineData("left", true, ViewportAlignMode.Left)]
        [InlineData("RIGHT", true, ViewportAlignMode.Right)]
        [InlineData(" top ", true, ViewportAlignMode.Top)]
        [InlineData("bottom", true, ViewportAlignMode.Bottom)]
        [InlineData("center_x", true, ViewportAlignMode.CenterX)]
        [InlineData("center_y", true, ViewportAlignMode.CenterY)]
        [InlineData("center", true, ViewportAlignMode.Center)]
        [InlineData("center_on_sheet", true, ViewportAlignMode.CenterOnSheet)]
        [InlineData("middle", false, ViewportAlignMode.Left)]
        [InlineData("", false, ViewportAlignMode.Left)]
        [InlineData(null, false, ViewportAlignMode.Left)]
        public void Mode_parsing_accepts_only_the_documented_names(string text, bool ok, ViewportAlignMode expected)
        {
            Assert.Equal(ok, ViewportLayoutMath.TryParseMode(text, out var mode));
            if (ok) Assert.Equal(expected, mode);
        }

        [Fact]
        public void Only_the_sheet_centre_mode_works_without_a_reference_viewport()
        {
            foreach (ViewportAlignMode mode in Enum.GetValues(typeof(ViewportAlignMode)))
                Assert.Equal(mode != ViewportAlignMode.CenterOnSheet, ViewportLayoutMath.NeedsReference(mode));
        }

        // Measured on Revit 2027 (Snowdon sample): a viewport bound to a saved position was moved
        // inside a committed transaction and put back at regeneration, so the commit alone proves nothing.
        [Theory]
        [InlineData(5, 5, "applied")]
        [InlineData(1, 1, "applied")]
        [InlineData(5, 0, "not_applied")]
        [InlineData(1, 0, "not_applied")]
        [InlineData(5, 3, "partially_applied")]
        [InlineData(2, 1, "partially_applied")]
        public void Apply_outcome_reflects_how_many_moves_Revit_kept(int planned, int held, string expected)
        {
            Assert.Equal(expected, ViewportLayoutMath.ClassifyApply(planned, held));
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(2, 3)]
        [InlineData(2, -1)]
        public void Apply_outcome_rejects_counts_that_cannot_happen(int planned, int held)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ViewportLayoutMath.ClassifyApply(planned, held));
        }
    }
}
