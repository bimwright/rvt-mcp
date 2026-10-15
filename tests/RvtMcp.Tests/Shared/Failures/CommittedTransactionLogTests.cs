using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    // Event sequences observed on Revit 2024 (RVT-F21 probe): a rolled-back group lists the
    // transactions it reverted, newest first; an assimilated inner group appears under its group name.
    public class CommittedTransactionLogTests
    {
        [Fact]
        public void Group_rollback_of_every_commit_leaves_no_committed_change()
        {
            var log = new CommittedTransactionLog();
            log.Committed(new[] { "a1" });
            log.Committed(new[] { "a2" });
            Assert.True(log.TryRevert(new[] { "a2", "a1" }));
            Assert.Equal(0, log.Count);
        }

        [Fact]
        public void Commit_before_the_rolled_back_group_is_kept()
        {
            var log = new CommittedTransactionLog();
            log.Committed(new[] { "c-outside" });
            log.Committed(new[] { "c-in" });
            Assert.True(log.TryRevert(new[] { "c-in" }));
            Assert.Equal(1, log.Count);
        }

        [Fact]
        public void Assimilated_inner_group_name_does_not_match_and_changes_nothing()
        {
            var log = new CommittedTransactionLog();
            log.Committed(new[] { "d-o" });
            log.Committed(new[] { "d-i" });
            Assert.False(log.TryRevert(new[] { "gD-inner", "d-o" }));
            Assert.Equal(2, log.Count);
        }

        [Fact]
        public void Reverting_more_than_was_committed_is_refused()
        {
            var log = new CommittedTransactionLog();
            log.Committed(new[] { "x" });
            Assert.False(log.TryRevert(new[] { "x", "earlier" }));
            Assert.Equal(1, log.Count);
        }

        [Fact]
        public void Repeated_names_are_matched_by_position()
        {
            var log = new CommittedTransactionLog();
            log.Committed(new[] { "same" });
            log.Committed(new[] { "other" });
            log.Committed(new[] { "same" });
            Assert.True(log.TryRevert(new[] { "same" }));
            Assert.Equal(2, log.Count);
            Assert.False(log.TryRevert(new[] { "same" }));
        }

        [Fact]
        public void Empty_revert_list_changes_nothing()
        {
            var log = new CommittedTransactionLog();
            log.Committed(new[] { "a" });
            Assert.True(log.TryRevert(new string[0]));
            Assert.Equal(1, log.Count);
        }
    }
}
