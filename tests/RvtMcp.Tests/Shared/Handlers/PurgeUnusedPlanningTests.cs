using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests.Shared.Handlers
{
    public sealed class PurgeUnusedPlanningTests
    {
        [Theory]
        [InlineData(500, 500, true)]
        [InlineData(100, 100, true)]
        [InlineData(50, 50, true)]
        public void Limit_truncation_never_inflates_skipped_due_to_references(int limit, int expectedReturned, bool expectedTruncated)
        {
            var candidates = Enumerable.Range(1, 600).Select(i => (long)i).ToList();
            var referenced = new HashSet<long> { 10, 20, 30, 40 };

            var plan = PurgeUnusedPlanning.Select(candidates, id => id, referenced, limit);

            Assert.Equal(600, plan.TotalCandidates);
            Assert.Equal(596, plan.SafeToPurge);
            Assert.Equal(4, plan.SkippedDueToReferences);
            Assert.Equal(expectedReturned, plan.Returned);
            Assert.Equal(expectedTruncated, plan.Truncated);
            Assert.Equal(expectedReturned, plan.Items.Count);
            Assert.DoesNotContain(10L, plan.Items);
        }

        [Fact]
        public void Exact_limit_match_is_not_truncated()
        {
            var candidates = new long[] { 1, 2, 3, 4, 5 };
            var referenced = new HashSet<long> { 2, 4 };

            var plan = PurgeUnusedPlanning.Select(candidates, id => id, referenced, limit: 3);

            Assert.Equal(5, plan.TotalCandidates);
            Assert.Equal(3, plan.SafeToPurge);
            Assert.Equal(2, plan.SkippedDueToReferences);
            Assert.Equal(3, plan.Returned);
            Assert.False(plan.Truncated);
            Assert.Equal(new long[] { 1, 3, 5 }, plan.Items);
        }

        [Fact]
        public void All_referenced_yields_zero_safe_and_full_skipped()
        {
            var candidates = new long[] { 1, 2, 3 };
            var referenced = new HashSet<long> { 1, 2, 3 };

            var plan = PurgeUnusedPlanning.Select(candidates, id => id, referenced, limit: 10);

            Assert.Equal(3, plan.TotalCandidates);
            Assert.Equal(0, plan.SafeToPurge);
            Assert.Equal(3, plan.SkippedDueToReferences);
            Assert.Equal(0, plan.Returned);
            Assert.False(plan.Truncated);
            Assert.Empty(plan.Items);
        }

        [Fact]
        public void Non_positive_limit_throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PurgeUnusedPlanning.Select(new long[] { 1 }, id => id, new HashSet<long>(), limit: 0));
        }
    }
}
