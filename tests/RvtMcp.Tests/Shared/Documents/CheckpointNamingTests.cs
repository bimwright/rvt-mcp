using System;
using System.IO;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class CheckpointNamingTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rvt-checkpoint-name-" + Guid.NewGuid().ToString("N"));
        private static readonly DateTimeOffset Local = new DateTimeOffset(2026, 11, 3, 15, 30, 12, TimeSpan.FromHours(7));

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Theory]
        [InlineData(".rfa", "20261103-153012.rfa")]
        [InlineData(".rte", "20261103-153012.rte")]
        [InlineData(".RTE", "20261103-153012.rte")]
        public void Name_uses_local_time_and_model_extension(string extension, string expected)
        {
            var name = CheckpointNaming.Next(directory, extension, Local, Array.Empty<CheckpointEntry>());
            Assert.Equal("20261103-153012", name.Id);
            Assert.Equal(expected, name.File);
            Assert.False(Directory.Exists(directory));
        }

        [Fact]
        public void Existing_files_and_index_entries_are_not_reused()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "20261103-153012.rvt"), "original");
            var entries = new[] { new CheckpointEntry { Id = "20261103-153012-2", File = "20261103-153012-2.rvt" } };
            var name = CheckpointNaming.Next(directory, ".rvt", Local, entries);
            Assert.Equal("20261103-153012-3", name.Id);
            Assert.Equal("original", File.ReadAllText(Path.Combine(directory, "20261103-153012.rvt")));
        }
    }
}
