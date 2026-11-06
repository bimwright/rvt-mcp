using System;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class FileVersionStatusTests
    {
        private static readonly Guid MemoryGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid OtherGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");

        [Fact]
        public void More_saves_on_disk_blocks_even_when_guids_match()
        {
            Assert.Equal("changed_on_disk", FileVersionStatus.Compare(5, MemoryGuid, 6, MemoryGuid));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Fewer_saves_on_disk_blocks_even_with_either_guid(bool sameGuid)
        {
            Assert.Equal("changed_on_disk", FileVersionStatus.Compare(5, MemoryGuid, 4, sameGuid ? MemoryGuid : OtherGuid));
        }

        [Fact]
        public void Equal_saves_are_in_sync_even_when_guids_differ()
        {
            // Revit changes the in-memory GUID on every edit and when opening an older format (SPEC r7, P4 F1).
            Assert.Equal("in_sync", FileVersionStatus.Compare(5, MemoryGuid, 5, OtherGuid));
        }

        [Fact]
        public void Equal_saves_and_guids_are_in_sync()
        {
            Assert.Equal("in_sync", FileVersionStatus.Compare(5, MemoryGuid, 5, MemoryGuid));
        }

        [Fact]
        public void Missing_disk_file_is_reported()
        {
            Assert.Equal("missing_on_disk", FileVersionStatus.Compare(5, MemoryGuid, null, null));
        }
    }
}
