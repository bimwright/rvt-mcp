using System;
using System.IO;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class BackupFileCounterTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rvt-backups-" + Guid.NewGuid().ToString("N"));
        public BackupFileCounterTests() => Directory.CreateDirectory(directory);
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        [Theory]
        [InlineData("Tower.v1.rvt")]
        [InlineData("Chair.rfa")]
        [InlineData("Template.rte")]
        public void Counts_only_matching_four_digit_backup_names(string model)
        {
            var stem = Path.GetFileNameWithoutExtension(model);
            var extension = Path.GetExtension(model);
            foreach (var name in new[]
            {
                stem + ".0001" + extension, stem.ToUpperInvariant() + ".9876" + extension.ToUpperInvariant(),
                stem + ".123" + extension, stem + ".12345" + extension,
                stem + ".00a1" + extension, stem + "-other.0001" + extension,
                stem + ".0001" + extension + ".old", stem + ".0001.txt"
            }) File.WriteAllText(Path.Combine(directory, name), "x");

            Assert.Equal(2, BackupFileCounter.Count(Path.Combine(directory, model)));
        }
    }
}
