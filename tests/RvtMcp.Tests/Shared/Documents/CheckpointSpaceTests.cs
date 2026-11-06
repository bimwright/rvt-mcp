using System.IO;
using System.Runtime.InteropServices;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class CheckpointSpaceTests
    {
        [Theory]
        [InlineData(1000L, 1099L, false)]
        [InlineData(1000L, 1100L, true)]
        [InlineData(1L, 1L, false)]
        [InlineData(1L, 2L, true)]
        [InlineData(0L, 0L, true)]
        [InlineData(long.MaxValue, long.MaxValue, false)]
        public void Requires_at_least_one_point_one_times_source_size(long sourceBytes, long freeBytes, bool expected)
        {
            Assert.Equal(expected, CheckpointSpace.HasEnough(sourceBytes, freeBytes));
        }

        [Fact]
        public void Available_space_is_read_from_the_model_directory_on_windows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            Assert.InRange(CheckpointSpace.AvailableBytes(Path.Combine(Path.GetTempPath(), "model.rvt")), 1, long.MaxValue);
        }
    }
}
