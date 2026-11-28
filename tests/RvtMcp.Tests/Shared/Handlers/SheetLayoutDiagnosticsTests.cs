using System;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests.Shared.Handlers
{
    public sealed class SheetLayoutDiagnosticsTests
    {
        [Fact]
        public void Detects_overlapping_viewports_and_top_title_block_overflow()
        {
            var titleBlock = new SheetRect(0, 0, 840, 594);
            var viewports = new[]
            {
                new SheetViewportBox
                {
                    VpId = 101,
                    ViewId = 1001,
                    ViewName = "Plan A",
                    BoxMm = new SheetRect(40, 60, 520, 520)
                },
                new SheetViewportBox
                {
                    VpId = 102,
                    ViewId = 1002,
                    ViewName = "Detail B",
                    BoxMm = new SheetRect(360, 340, 780, 630)
                }
            };

            var result = SheetLayoutDiagnostics.Analyze(titleBlock, viewports);

            var overlap = Assert.Single(result.BoxOverlaps);
            Assert.Equal(101, overlap.VpIdA);
            Assert.Equal(102, overlap.VpIdB);
            Assert.Equal(160.0, overlap.OverlapWidthMm);
            Assert.Equal(180.0, overlap.OverlapHeightMm);

            var outside = Assert.Single(result.OutsideTitleBlock);
            Assert.Equal(102, outside.VpId);
            Assert.Equal("Detail B", outside.ViewName);
            Assert.Equal(0.0, outside.OverflowLeftMm);
            Assert.Equal(0.0, outside.OverflowRightMm);
            Assert.Equal(36.0, outside.OverflowTopMm);
            Assert.Equal(0.0, outside.OverflowBottomMm);
            Assert.Equal(36.0, outside.MaxOverflowMm);
        }

        [Fact]
        public void Clean_sheet_within_tolerance_reports_no_issues()
        {
            var titleBlock = new SheetRect(10, 20, 850, 614);
            var viewports = new[]
            {
                new SheetViewportBox { VpId = 1, ViewId = 11, ViewName = "Plan", BoxMm = new SheetRect(9.5, 50, 400, 500) },
                new SheetViewportBox { VpId = 2, ViewId = 12, ViewName = "Detail", BoxMm = new SheetRect(400.5, 50, 750, 500) }
            };

            var result = SheetLayoutDiagnostics.Analyze(titleBlock, viewports);

            Assert.Empty(result.BoxOverlaps);
            Assert.Empty(result.OutsideTitleBlock);
        }

        [Fact]
        public void Missing_title_block_still_detects_viewport_overlaps()
        {
            var viewports = new[]
            {
                new SheetViewportBox { VpId = 1, ViewId = 11, ViewName = "A", BoxMm = new SheetRect(0, 0, 200, 200) },
                new SheetViewportBox { VpId = 2, ViewId = 12, ViewName = "B", BoxMm = new SheetRect(150, 150, 300, 300) }
            };

            var result = SheetLayoutDiagnostics.Analyze(null, viewports);

            Assert.Single(result.BoxOverlaps);
            Assert.Empty(result.OutsideTitleBlock);
        }

        [Fact]
        public void Non_zero_origin_title_block_detects_left_and_bottom_overflow()
        {
            var titleBlock = new SheetRect(100, 50, 940, 644);
            var viewports = new[]
            {
                new SheetViewportBox { VpId = 7, ViewId = 70, ViewName = "Shifted", BoxMm = new SheetRect(85, 30, 300, 300) }
            };

            var result = SheetLayoutDiagnostics.Analyze(titleBlock, viewports);

            var outside = Assert.Single(result.OutsideTitleBlock);
            Assert.Equal(15.0, outside.OverflowLeftMm);
            Assert.Equal(20.0, outside.OverflowBottomMm);
            Assert.Equal(20.0, outside.MaxOverflowMm);
        }

        [Fact]
        public void Zero_tolerance_ignores_microscopic_overlap_that_rounds_to_zero()
        {
            var viewports = new[]
            {
                new SheetViewportBox { VpId = 1, ViewId = 11, ViewName = "A", BoxMm = new SheetRect(0, 0, 100.002, 100) },
                new SheetViewportBox { VpId = 2, ViewId = 12, ViewName = "B", BoxMm = new SheetRect(100.0, 0, 200, 100) }
            };

            var result = SheetLayoutDiagnostics.Analyze(null, viewports, toleranceMm: 0.0);

            Assert.Empty(result.BoxOverlaps);
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Invalid_tolerance_throws(double toleranceMm)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SheetLayoutDiagnostics.Analyze(null, Array.Empty<SheetViewportBox>(), toleranceMm: toleranceMm));
        }

        [Fact]
        public void Null_viewports_throws()
        {
            Assert.Throws<ArgumentNullException>(() => SheetLayoutDiagnostics.Analyze(null, null));
        }
    }
}
