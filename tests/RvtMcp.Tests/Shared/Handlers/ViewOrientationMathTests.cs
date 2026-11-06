using System;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Input checks and up-vector fallback behind revit_set_view_orientation and revit_create_camera_view.
    /// Runs without Revit because ViewOrientationMath has no API references.
    /// </summary>
    public class ViewOrientationMathTests
    {
        [Fact]
        public void Three_numbers_are_read()
        {
            var v = ViewOrientationMath.ReadVector(JObject.Parse("{\"eye\":[1,2.5,-3]}"), "eye", true, " in mm", out var error);
            Assert.Null(error);
            Assert.Equal(new[] { 1, 2.5, -3 }, v);
        }

        [Theory]
        [InlineData("{\"eye\":[1,2]}")]
        [InlineData("{\"eye\":[1,2,3,4]}")]
        [InlineData("{\"eye\":[1,\"2\",3]}")]
        [InlineData("{\"eye\":\"1,2,3\"}")]
        public void Anything_but_three_numbers_is_refused(string json)
        {
            Assert.Null(ViewOrientationMath.ReadVector(JObject.Parse(json), "eye", true, " in mm", out var error));
            Assert.Equal("eye must be [x,y,z] of three finite numbers in mm.", error);
        }

        [Fact]
        public void Non_finite_number_is_refused()
        {
            var request = new JObject { ["up"] = new JArray(0.0, double.NaN, 1.0) };
            Assert.Null(ViewOrientationMath.ReadVector(request, "up", false, "", out var error));
            Assert.NotNull(error);
        }

        [Fact]
        public void Missing_key_is_an_error_only_when_required()
        {
            var empty = new JObject();
            Assert.Null(ViewOrientationMath.ReadVector(empty, "up", false, "", out var optionalError));
            Assert.Null(optionalError);
            Assert.Null(ViewOrientationMath.ReadVector(empty, "target", true, " in mm", out var requiredError));
            Assert.Equal("target is required as [x,y,z] in mm.", requiredError);
        }

        [Fact]
        public void Requested_up_is_made_perpendicular_to_the_view_direction()
        {
            var up = ViewOrientationMath.PerpendicularUp(new double[] { 1, 0, -1 }, new double[] { 0, 0, 1 });
            Assert.Equal(0, up[0] * 1 + up[2] * -1, 9);
            Assert.Equal(1, Math.Sqrt(up[0] * up[0] + up[1] * up[1] + up[2] * up[2]), 9);
            Assert.True(up[2] > 0);
        }

        [Fact]
        public void Looking_straight_down_falls_back_to_plus_y()
        {
            var up = ViewOrientationMath.PerpendicularUp(new double[] { 0, 0, -5 }, null);
            Assert.Equal(new double[] { 0, 1, 0 }, up);
        }

        [Fact]
        public void Up_parallel_to_a_level_view_falls_back_to_plus_z()
        {
            var up = ViewOrientationMath.PerpendicularUp(new double[] { 0, 1, 0 }, new double[] { 0, 1, 0 });
            Assert.Equal(new double[] { 0, 0, 1 }, up);
        }

        [Fact]
        public void Same_eye_and_target_has_no_up()
        {
            Assert.Null(ViewOrientationMath.PerpendicularUp(new double[] { 0, 0, 0 }, null));
        }
    }
}
