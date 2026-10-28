using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Reports.Navigation;
using Xunit;

namespace RvtMcp.Tests.Reports
{
    public class ReportNavigationTests
    {
        private static ViewCandidate Plan(long id, int inside, double area = double.PositiveInfinity) =>
            new ViewCandidate(id, ViewCandidateKind.Plan, inside, area);

        [Fact]
        public void Order_puts_plans_first_then_the_default_3D_then_sections()
        {
            var ordered = ViewSearch.Order(new[]
            {
                new ViewCandidate(1, ViewCandidateKind.SectionOrElevation, 5, 10),
                new ViewCandidate(2, ViewCandidateKind.Default3D, 5, double.PositiveInfinity),
                Plan(3, 1),
            });
            Assert.Equal(new long[] { 3, 2, 1 }, ordered.Select(c => c.Id));
        }

        [Fact]
        public void Within_a_kind_more_elements_come_first_then_the_smaller_crop_and_uncropped_last()
        {
            var ordered = ViewSearch.Order(new[] { Plan(1, 1), Plan(2, 2, 500), Plan(3, 2, 100), Plan(4, 1, 50) });
            Assert.Equal(new long[] { 3, 2, 4, 1 }, ordered.Select(c => c.Id));
        }

        [Fact]
        public void Order_drops_views_without_elements_inside_and_nulls()
        {
            var ordered = ViewSearch.Order(new[] { Plan(1, 0), null, Plan(2, 1) });
            Assert.Equal(new long[] { 2 }, ordered.Select(c => c.Id));
            Assert.Empty(ViewSearch.Order(null));
        }

        [Fact]
        public void FindFirst_returns_the_first_view_that_shows_an_element()
        {
            var ordered = ViewSearch.Order(new[] { Plan(1, 3), Plan(2, 2), Plan(3, 1) });
            var checkedIds = new List<long>();
            var hit = ViewSearch.FindFirst(ordered, c => { checkedIds.Add(c.Id); return c.Id == 2; }, () => TimeSpan.Zero, ViewSearch.Budget);
            Assert.Equal(2, hit.Id);
            Assert.Equal(new long[] { 1, 2 }, checkedIds);
        }

        [Fact]
        public void Past_the_budget_only_the_default_3D_is_still_checked()
        {
            var ordered = ViewSearch.Order(new[]
            {
                Plan(1, 3), Plan(2, 2),
                new ViewCandidate(3, ViewCandidateKind.Default3D, 1, double.PositiveInfinity),
                new ViewCandidate(4, ViewCandidateKind.Default3D, 1, double.PositiveInfinity),
                new ViewCandidate(5, ViewCandidateKind.SectionOrElevation, 1, 10),
            });
            var elapsed = TimeSpan.Zero;
            var checkedIds = new List<long>();
            var hit = ViewSearch.FindFirst(ordered, c =>
            {
                checkedIds.Add(c.Id);
                elapsed += TimeSpan.FromSeconds(3); // every check is slow
                return false;
            }, () => elapsed, ViewSearch.Budget);
            Assert.Null(hit);
            Assert.Equal(new long[] { 1, 3 }, checkedIds);
        }

        [Fact]
        public void One_view_is_checked_even_when_the_budget_is_already_spent()
        {
            var checkedIds = new List<long>();
            var hit = ViewSearch.FindFirst(new[] { Plan(1, 1), Plan(2, 1) }, c => { checkedIds.Add(c.Id); return true; },
                () => TimeSpan.FromMinutes(1), ViewSearch.Budget);
            Assert.Equal(1, hit.Id);
            Assert.Equal(new long[] { 1 }, checkedIds);
        }

        [Fact]
        public void Sections_are_checked_while_time_remains()
        {
            var ordered = ViewSearch.Order(new[] { Plan(1, 1), new ViewCandidate(2, ViewCandidateKind.SectionOrElevation, 1, 10) });
            var hit = ViewSearch.FindFirst(ordered, c => c.Id == 2, () => TimeSpan.FromMilliseconds(100), ViewSearch.Budget);
            Assert.Equal(2, hit.Id);
        }

        [Fact]
        public void Latest_request_slot_keeps_only_the_newest_click()
        {
            var slot = new LatestRequestSlot<string>();
            Assert.Null(slot.Take());
            slot.Put("first");
            slot.Put("second");
            Assert.Equal("second", slot.Take());
            Assert.Null(slot.Take());
        }
    }
}
