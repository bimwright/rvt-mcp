using System.Collections.Generic;
using RvtMcp.Plugin.Lint;
using Xunit;

namespace RvtMcp.Tests.Lint
{
    public class ViewNamingAnalyzerTests
    {
        [Theory]
        [InlineData("L01-Lobby", "L{NN}-{Name}")]
        [InlineData("L02-Office", "L{NN}-{Name}")]
        [InlineData("Level 1", "{Name} {NN}")]
        [InlineData("Site", "{Name}")]
        [InlineData("3D View 1", "3D {Name} {NN}")]
        [InlineData("Plan-Level-01", "Plan-{Name}-{NN}")]
        public void Tokenize_produces_expected_pattern(string input, string expected)
        {
            var pattern = ViewNamingAnalyzer.Tokenize(input);
            Assert.Equal(expected, pattern);
        }

        [Fact]
        public void Analyze_empty_list_returns_empty_patterns_and_null_dominant()
        {
            var result = ViewNamingAnalyzer.Analyze(new string[0]);
            Assert.Equal(0, result.TotalViews);
            Assert.Empty(result.Patterns);
            Assert.Null(result.Dominant);
            Assert.Empty(result.Outliers);
        }

        [Fact]
        public void Analyze_all_matching_returns_single_pattern_coverage_one()
        {
            var names = new[] { "L01-Lobby", "L02-Office", "L03-Roof", "L04-Plant", "L05-Basement" };
            var result = ViewNamingAnalyzer.Analyze(names);
            Assert.Equal(5, result.TotalViews);
            Assert.Single(result.Patterns);
            Assert.Equal("L{NN}-{Name}", result.Patterns[0].Pattern);
            Assert.Equal(5, result.Patterns[0].Count);
            Assert.Equal(1.0, result.Patterns[0].Coverage, 2);
            Assert.Equal("L{NN}-{Name}", result.Dominant);
        }

        [Fact]
        public void Analyze_majority_with_outliers_picks_dominant()
        {
            var names = new[]
            {
                "L01-Lobby", "L02-Office", "L03-Roof", "L04-Plant",
                "L05-Basement", "L06-Storage", "L07-Garage",  // 7 matching
                "Site", "Overview", "Key Plan"                // 3 outliers
            };
            var result = ViewNamingAnalyzer.Analyze(names);
            Assert.Equal(10, result.TotalViews);
            Assert.Equal("L{NN}-{Name}", result.Dominant);
            Assert.Equal(0.70, result.Patterns[0].Coverage, 2);
        }

        [Fact]
        public void Analyze_no_majority_returns_null_dominant()
        {
            var names = new[] { "L01-X", "L02-Y", "A-B", "C-D", "Foo", "Bar", "Baz 1", "Baz 2", "Baz 3", "Baz 4" };
            var result = ViewNamingAnalyzer.Analyze(names);
            // No single pattern has ≥50% coverage here
            Assert.Null(result.Dominant);
            Assert.True(result.Patterns.Count >= 2);
        }

        [Fact]
        public void Analyze_outliers_sorted_by_edit_distance_closest_first()
        {
            var names = new[]
            {
                "L01-Lobby", "L02-Office", "L03-Roof", "L04-Plant",
                "L05-Basement", "L06-Storage", "L07-Garage",  // dominant
                "Level 1",                                     // close to dominant (token-count diff 0-1)
                "Completely Different Name 42"                 // far from dominant
            };
            var result = ViewNamingAnalyzer.Analyze(names);
            Assert.Equal("L{NN}-{Name}", result.Dominant);
            // Only close-to-dominant outliers returned
            Assert.Single(result.Outliers);
            Assert.Equal("Level 1", result.Outliers[0].Name);
            Assert.Equal("L{NN}-{Name}", result.Outliers[0].ClosestPattern);
        }

        [Fact]
        public void Analyze_custom_outlier_limit_supports_handler_paging()
        {
            var names = new List<string>();
            for (int i = 1; i <= 40; i++) names.Add($"L{i:00}-View");
            for (int i = 1; i <= 21; i++) names.Add($"Level {i}");

            var result = ViewNamingAnalyzer.Analyze(names, outlierLimit: 21);

            Assert.Equal(21, result.Outliers.Count);
        }

        [Fact]
        public void Analyze_outliers_capped_at_20()
        {
            // 21 "almost-matching" outliers vs 40 matching → outliers list stops at 20
            var names = new List<string>();
            for (int i = 1; i <= 40; i++) names.Add($"L{i:00}-View");
            for (int i = 1; i <= 21; i++) names.Add($"Level {i}");  // each tokenizes to "{Name} {NN}"
            var result = ViewNamingAnalyzer.Analyze(names);
            Assert.Equal("L{NN}-{Name}", result.Dominant);
            Assert.Equal(20, result.Outliers.Count);
        }
    }
}
