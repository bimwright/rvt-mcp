using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class CheckpointPlannerTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rvt-planner-" + Guid.NewGuid().ToString("N") + ".checkpoints");

        public CheckpointPlannerTests() => Directory.CreateDirectory(directory);
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        private CheckpointEntry Entry(string id, string createdUtc, bool milestone = false, bool exists = true)
        {
            var item = new CheckpointEntry { Id = id, File = id + ".rvt", CreatedUtc = createdUtc, Milestone = milestone };
            if (exists) File.WriteAllText(Path.Combine(directory, item.File), id);
            return item;
        }

        [Fact]
        public void Keeps_milestones_and_newest_existing_entries_by_utc_and_reports_missing()
        {
            var entries = new[]
            {
                Entry("20300101-000000", "2026-01-01T00:00:00Z"),
                Entry("20280101-000000", "2026-02-01T00:00:00Z"),
                Entry("20240101-000000", "2026-03-01T00:00:00Z"),
                Entry("20200101-000000", "2020-01-01T00:00:00Z", milestone: true),
                Entry("20290101-000000", "2026-04-01T00:00:00Z", exists: false)
            };
            File.WriteAllText(Path.Combine(directory, "untracked.rvt"), "keep");

            var plan = CheckpointPlanner.Plan(directory, entries, 2);

            Assert.Equal("20300101-000000", Assert.Single(plan.DeleteFiles).Id);
            Assert.Equal("20290101-000000", Assert.Single(plan.MissingEntries).Id);
            Assert.Equal(entries[0].Id.Length, plan.BytesToFree);
            Assert.True(File.Exists(Path.Combine(directory, "untracked.rvt")));
            Assert.True(File.Exists(Path.Combine(directory, entries[3].File)));
        }

        [Fact]
        public void Hash_is_order_independent_and_changes_with_candidate_metadata()
        {
            var entries = new[]
            {
                Entry("20260101-000000", "2026-01-01T00:00:00Z"),
                Entry("20260201-000000", "2026-02-01T00:00:00Z")
            };
            var first = CheckpointPlanner.Plan(directory, entries, 1);
            var reordered = CheckpointPlanner.Plan(directory, entries.AsEnumerable().Reverse(), 1);
            Assert.Equal(first.PlanHash, reordered.PlanHash);
            Assert.Matches("^[0-9a-f]{64}$", first.PlanHash);
            File.AppendAllText(Path.Combine(directory, entries[0].File), "changed");
            Assert.NotEqual(first.PlanHash, CheckpointPlanner.Plan(directory, entries, 1).PlanHash);
        }

        [Fact]
        public void Hash_changes_when_keep_last_changes_even_with_no_candidates()
        {
            var entries = new[] { Entry("20260101-000000", "2026-01-01T00:00:00Z") };
            Assert.NotEqual(CheckpointPlanner.Plan(directory, entries, 1).PlanHash,
                CheckpointPlanner.Plan(directory, entries, 2).PlanHash);
        }

        [Theory]
        [InlineData("..\\outside.rvt")]
        [InlineData("C:\\outside.rvt")]
        [InlineData("nested\\outside.rvt")]
        public void Refuses_index_paths_outside_or_below_checkpoint_directory(string file)
        {
            var entries = new[] { new CheckpointEntry { Id = "a", File = file, CreatedUtc = "2026-01-01T00:00:00Z" } };
            Assert.Throws<InvalidDataException>(() => CheckpointPlanner.Plan(directory, entries, 1));
        }
    }
}
