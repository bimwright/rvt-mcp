using RvtMcp.Plugin;
using RvtMcp.Plugin.Reports.Views;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests.Reports
{
    public class ReportsWindowPlacementTests
    {
        private static readonly ToastBounds Work = new ToastBounds(0, 0, 1920, 1040);
        private static readonly ToastBounds Owner = new ToastBounds(100, 50, 1600, 900);

        [Fact]
        public void Sizes_and_minimums_follow_the_plan()
        {
            Assert.Equal(720, ReportsWindowPlacement.MinWidth(ReportsWindowMode.Wide));
            Assert.Equal(460, ReportsWindowPlacement.MinHeight(ReportsWindowMode.Wide));
            Assert.Equal(double.PositiveInfinity, ReportsWindowPlacement.MaxWidth(ReportsWindowMode.Wide));
            Assert.Equal(360, ReportsWindowPlacement.MinWidth(ReportsWindowMode.Compact));
            Assert.Equal(460, ReportsWindowPlacement.MinHeight(ReportsWindowMode.Compact));
            Assert.Equal(560, ReportsWindowPlacement.MaxWidth(ReportsWindowMode.Compact));
        }

        [Fact]
        public void Compact_never_restores_wider_than_its_maximum_but_wide_does()
        {
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Compact,
                new ReportsWindowBounds(100, 100, 1100, 700), Owner, Work), 100, 100, 560, 700);
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Wide,
                new ReportsWindowBounds(100, 100, 1600, 900), Owner, Work), 100, 100, 1600, 900);
        }

        [Fact]
        public void Default_wide_is_centred_on_the_owner()
        {
            AssertBounds(ReportsWindowPlacement.Default(ReportsWindowMode.Wide, Owner, Work), 505, 230, 790, 540);
        }

        [Fact]
        public void Default_wide_stays_inside_the_work_area_when_the_owner_hangs_off_screen()
        {
            var owner = new ToastBounds(-900, -300, 1000, 700);
            AssertBounds(ReportsWindowPlacement.Default(ReportsWindowMode.Wide, owner, Work), 0, 0, 790, 540);
        }

        [Fact]
        public void Default_compact_hugs_the_owner_right_edge_and_is_centred_vertically()
        {
            AssertBounds(ReportsWindowPlacement.Default(ReportsWindowMode.Compact, Owner, Work), 1294, 195, 390, 610);
            var shortOwner = new ToastBounds(100, 50, 1600, 500);
            AssertBounds(ReportsWindowPlacement.Default(ReportsWindowMode.Compact, shortOwner, Work), 1294, 66, 390, 468);
        }

        [Fact]
        public void Small_owner_uses_the_minimum_size()
        {
            var owner = new ToastBounds(200, 200, 600, 300);
            AssertBounds(ReportsWindowPlacement.Default(ReportsWindowMode.Compact, owner, Work), 394, 120, 390, 460);
            var wide = ReportsWindowPlacement.Default(ReportsWindowMode.Wide, owner, Work);
            Assert.Equal(790, wide.Width);
            Assert.Equal(540, wide.Height);
        }

        [Fact]
        public void Saved_bounds_inside_the_work_area_are_kept()
        {
            var saved = new ReportsWindowBounds(1500.5, 140, 390, 610);
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Compact, saved, Owner, Work), 1500.5, 140, 390, 610);
            var second = new ToastBounds(1920, -200, 1280, 1000);
            var onSecond = new ReportsWindowBounds(2000, -100, 900, 600);
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Wide, onSecond, Owner, second), 2000, -100, 900, 600);
        }

        [Fact]
        public void Saved_bounds_off_screen_are_clamped_into_the_work_area()
        {
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Compact,
                new ReportsWindowBounds(5000, -300, 390, 610), Owner, Work), 1530, 0, 390, 610);
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Wide,
                new ReportsWindowBounds(-4000, 9000, 790, 540), Owner, Work), 0, 500, 790, 540);
        }

        [Fact]
        public void Bounds_larger_than_the_work_area_shrink_to_it_but_not_below_the_minimum()
        {
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Wide,
                new ReportsWindowBounds(10, 10, 3000, 2000), Owner, Work), 0, 0, 1920, 1040);
            var tiny = new ToastBounds(0, 0, 600, 300);
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Wide,
                new ReportsWindowBounds(10, 10, 3000, 2000), Owner, tiny), 0, 0, 720, 460);
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Compact,
                new ReportsWindowBounds(10, 10, 100, 100), Owner, Work), 10, 10, 360, 460);
        }

        [Fact]
        public void Missing_or_invalid_saved_bounds_use_the_default()
        {
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Wide, null, Owner, Work), 505, 230, 790, 540);
            AssertBounds(ReportsWindowPlacement.Restore(ReportsWindowMode.Compact,
                new ReportsWindowBounds(double.NaN, 0, 390, 610), Owner, Work), 1294, 195, 390, 610);
        }

        [Fact]
        public void Owner_with_non_finite_or_empty_values_falls_back_to_the_work_area()
        {
            var work = new ToastBounds(100, 0, 1600, 1000);
            foreach (var owner in new ToastBounds?[] { null, new ToastBounds(double.NaN, 0, 800, 600),
                new ToastBounds(0, double.PositiveInfinity, 800, 600), new ToastBounds(0, 0, double.NaN, 600), new ToastBounds(0, 0, 0, 600) })
            {
                AssertBounds(ReportsWindowPlacement.Default(ReportsWindowMode.Wide, owner, work), 505, 230, 790, 540);
                AssertBounds(ReportsWindowPlacement.Default(ReportsWindowMode.Compact, owner, work), 1294, 195, 390, 610);
            }
        }

        private static void AssertBounds(ReportsWindowBounds bounds, double left, double top, double width, double height)
        {
            Assert.NotNull(bounds);
            Assert.Equal(left, bounds.Left, 6);
            Assert.Equal(top, bounds.Top, 6);
            Assert.Equal(width, bounds.Width, 6);
            Assert.Equal(height, bounds.Height, 6);
        }
    }
}
