using System.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests.Server.Memory
{
    public class DocumentChangeAccumulatorReportTests
    {
        [Fact]
        public void Accessor_returns_sorted_final_values_and_hides_incomplete_data()
        {
            var changes = new DocumentChangeAccumulator();
            changes.Observe(9, "added", "old", "uid");
            changes.Observe(9, "modified", "new");
            changes.Observe(2, "added");
            changes.Observe(3, "deleted");

            var added = changes.ReportElements("added").ToArray();

            Assert.False(changes.IsIncomplete);
            Assert.Equal(new[] { 2L, 9L }, added.Select(x => x.Id));
            Assert.Equal("new", added[1].Category);
            Assert.Equal("uid", added[1].UniqueId);
            Assert.Single(changes.ReportElements("deleted"));
            changes.MarkIncomplete("rollback");
            Assert.True(changes.IsIncomplete);
            Assert.Empty(changes.ReportElements("added"));
        }
    }
}
