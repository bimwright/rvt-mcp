using System.Collections.Generic;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Tag tools refuse a sheet and point the caller at the model views on its viewports (#42).
    /// </summary>
    public class TagViewPolicyTests
    {
        [Fact]
        public void Sheet_is_refused_and_the_placed_views_are_named_with_their_ids()
        {
            var error = TagViewPolicy.SheetRefusal("DrawingSheet", "A102 - Plans", new[]
            {
                new KeyValuePair<long, string>(312, "Level 1"),
                new KeyValuePair<long, string>(245424, "Level 2"),
            });

            Assert.NotNull(error);
            Assert.Contains("'A102 - Plans' is a sheet", error);
            Assert.Contains("Level 1 (viewId 312)", error);
            Assert.Contains("Level 2 (viewId 245424)", error);
        }

        [Fact]
        public void Sheet_without_viewports_is_refused_and_says_no_view_is_placed()
        {
            var error = TagViewPolicy.SheetRefusal("DrawingSheet", "Title Sheet", new KeyValuePair<long, string>[0]);

            Assert.NotNull(error);
            Assert.Contains("No model view is placed on it.", error);
        }

        [Theory]
        [InlineData("FloorPlan")]
        [InlineData("Section")]
        [InlineData("ThreeD")]
        [InlineData("DraftingView")]
        public void Model_and_drafting_views_are_not_refused(string viewType)
        {
            Assert.Null(TagViewPolicy.SheetRefusal(viewType, "Any", null));
        }
    }
}
