using System.Linq;
using RvtMcp.Plugin.ToolBaker;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class BakedToolRuntimeSourceTests
    {
        [Fact]
        public void BuildPreset_round_trips_handler_tool_and_fixed_args()
        {
            var source = BakedToolRuntimeSource.BuildPreset(
                "create_level",
                JObject.Parse(@"{""elevation"":0,""name"":""Level 02""}"));

            Assert.True(BakedToolRuntimeSource.HasMarker(source));
            Assert.True(BakedToolRuntimeSource.TryParse(source, out var spec));
            Assert.Equal("preset", spec.Kind);
            Assert.Equal("create_level", spec.Tool);
            Assert.Equal(@"{""elevation"":0,""name"":""Level 02""}", spec.FixedArgsJson);
            Assert.Empty(spec.Sequence);
            Assert.DoesNotContain("CommandResult.Ok", source);
            Assert.DoesNotContain("CommandResult.Fail", source);
        }

        [Fact]
        public void BuildMacro_round_trips_sequence()
        {
            var source = BakedToolRuntimeSource.BuildMacro(new[] { "create_level", "create_grid" });

            Assert.True(BakedToolRuntimeSource.HasMarker(source));
            Assert.True(BakedToolRuntimeSource.TryParse(source, out var spec));
            Assert.Equal("macro", spec.Kind);
            Assert.Equal(new[] { "create_level", "create_grid" }, spec.Sequence.ToArray());
            Assert.DoesNotContain("CommandResult.Ok", source);
            Assert.DoesNotContain("CommandResult.Fail", source);
        }

        [Fact]
        public void Runtime_markers_are_allowed_only_for_preset_and_macro_sources()
        {
            Assert.True(BakedToolRuntimeSource.IsAllowedForSuggestionSource("preset"));
            Assert.True(BakedToolRuntimeSource.IsAllowedForSuggestionSource("macro"));
            Assert.False(BakedToolRuntimeSource.IsAllowedForSuggestionSource("send_code"));
            Assert.False(BakedToolRuntimeSource.IsAllowedForSuggestionSource(""));
        }
    }
}
