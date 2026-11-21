using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// place_view_on_sheet takes an optional viewport centre in sheet paper mm: both coordinates or neither (#47).
    /// </summary>
    public class SheetPointInputTests
    {
        [Fact]
        public void No_point_is_accepted_and_means_title_block_centre()
        {
            Assert.Null(SheetPointInput.Validate(null, null));
        }

        [Fact]
        public void Both_coordinates_are_accepted()
        {
            Assert.Null(SheetPointInput.Validate(420, 297));
        }

        [Theory]
        [InlineData(420.0, null)]
        [InlineData(null, 297.0)]
        public void One_coordinate_alone_is_refused(double? x, double? y)
        {
            Assert.Contains("both xMm and yMm", SheetPointInput.Validate(x, y));
        }

        [Theory]
        [InlineData(double.NaN, 1.0)]
        [InlineData(1.0, double.PositiveInfinity)]
        public void Non_finite_coordinates_are_refused(double x, double y)
        {
            Assert.Contains("finite", SheetPointInput.Validate(x, y));
        }
    }
}
