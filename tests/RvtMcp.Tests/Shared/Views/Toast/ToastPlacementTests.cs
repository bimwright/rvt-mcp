using RvtMcp.Plugin;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ToastPlacementTests
    {
        [Theory]
        [InlineData(false, false, 116, 272)]
        [InlineData(true, false, 768, 272)]
        [InlineData(false, true, 116, 883)]
        [InlineData(true, true, 768, 883)]
        public void Four_corners_preserve_owner_relative_margin_and_top_corners_clear_the_ribbon_tabs(bool right, bool bottom, double left, double top)
        {
            var card = ToastPlacement.Anchor(new ToastBounds(100, 200, 1000, 800), 316, 101, new ToastPositionOptions(right, bottom));
            Assert.Equal(left, card.Left);
            Assert.Equal(top, card.Top);
        }

        [Fact]
        public void Default_position_is_top_left_below_the_ribbon_tab_row()
        {
            var card = ToastPlacement.Anchor(new ToastBounds(0, 0, 1920, 1080), 316, 101, new ToastPositionOptions());
            Assert.Equal(16, card.Left);
            Assert.Equal(72, card.Top);
        }

        [Fact]
        public void Offset_tracks_owner_and_corner_change_clears_it_but_drag_off_keeps_it()
        {
            var options = new ToastPositionOptions(offsetX: 45, offsetY: 60, dragEnabled: true);
            var card = ToastPlacement.Anchor(new ToastBounds(-1920, 0, 1920, 1080), 316, 101, options);
            Assert.Equal(-1859, card.Left);
            Assert.Equal(132, card.Top);
            Assert.True(options.WithDrag(false).HasOffset);
            // Drag off ignores the offset without discarding it.
            Assert.Equal(-1904, ToastPlacement.Anchor(new ToastBounds(-1920, 0, 1920, 1080), 316, 101, options.WithDrag(false)).Left);
            Assert.True(options.WithCorner(false, false).HasOffset);
            Assert.False(options.WithCorner(true, false).HasOffset);
            Assert.False(options.WithOffset(null, null).HasOffset);
        }

        [Theory]
        [InlineData(double.NaN, 1)]
        [InlineData(1, double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity, 1)]
        public void Invalid_offsets_are_ignored(double x, double y) =>
            Assert.False(new ToastPositionOptions(offsetX: x, offsetY: y).HasOffset);

        [Fact]
        public void A_half_specified_offset_is_ignored()
        {
            Assert.False(new ToastPositionOptions(offsetX: 5).HasOffset);
            Assert.False(new ToastPositionOptions(offsetY: 5).HasOffset);
        }

        [Fact]
        public void Monitor_removal_and_oversized_cards_have_finite_clamped_positions()
        {
            var work = new ToastBounds(0, 0, 1200, 700);
            var card = ToastPlacement.Clamp(new ToastBounds(-2000, 1200, 316, 258), work);
            Assert.Equal(0, card.Left);
            Assert.Equal(442, card.Top);
            var oversized = ToastPlacement.Clamp(new ToastBounds(double.NaN, double.PositiveInfinity, 2000, 900), work);
            Assert.Equal(0, oversized.Left);
            Assert.Equal(0, oversized.Top);
        }

        [Theory]
        [InlineData(16, false, false)]
        [InlineData(570, false, true)]
        [InlineData(16, true, false)]
        [InlineData(570, true, true)]
        public void Expansion_reverses_when_preferred_side_has_no_space(double top, bool preferUp, bool expected) =>
            Assert.Equal(expected, ToastPlacement.GrowUp(new ToastBounds(16, top, 316, 101), new ToastBounds(0, 0, 1200, 700), 157, preferUp));

        [Fact]
        public void Drag_threshold_does_not_turn_a_click_into_a_drag()
        {
            Assert.False(ToastPlacement.DragThreshold(3, -3, 4, 4));
            Assert.True(ToastPlacement.DragThreshold(4, 0, 4, 4));
            Assert.True(ToastPlacement.DragThreshold(0, -4, 4, 4));
        }
    }
}
