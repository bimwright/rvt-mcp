using System;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class ScheduleSortInputTests
    {
        [Theory]
        [InlineData("{}", true)]
        [InlineData("{\"ascending\":true}", true)]
        [InlineData("{\"ascending\":false}", false)]
        [InlineData("{\"order\":\"descending\"}", false)]
        [InlineData("{\"sortOrder\":\"Descending\"}", false)]
        [InlineData("{\"order\":\"Ascending\"}", true)]
        [InlineData("{\"sortOrder\":\" ascending \"}", true)]
        [InlineData("{\"ascending\":false,\"order\":\"descending\",\"sortOrder\":\"Descending\"}", false)]
        public void Recognized_directions_are_applied(string json, bool expected)
            => Assert.Equal(expected, ScheduleSortInput.Ascending(JObject.Parse(json)));

        [Theory]
        [InlineData("{\"ascending\":true,\"order\":\"descending\"}")]
        [InlineData("{\"order\":\"Ascending\",\"sortOrder\":\"Descending\"}")]
        [InlineData("{\"order\":\"sideways\"}")]
        [InlineData("{\"sortOrder\":2}")]
        public void Invalid_or_conflicting_direction_is_not_silently_ignored(string json)
            => Assert.Throws<ArgumentException>(() => ScheduleSortInput.Ascending(JObject.Parse(json)));
    }
}
