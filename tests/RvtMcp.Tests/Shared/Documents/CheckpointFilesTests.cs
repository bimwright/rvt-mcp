using System;
using System.IO;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class CheckpointFilesTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rvt-checkpoint-files-" + Guid.NewGuid().ToString("N"));
        public CheckpointFilesTests() => Directory.CreateDirectory(directory);
        public void Dispose()
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.GetFiles(directory)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }

        [Fact]
        public void Copy_preserves_source_and_clears_read_only_on_new_copy()
        {
            var source = Path.Combine(directory, "source.rvt");
            var target = Path.Combine(directory, "checkpoint.rvt");
            File.WriteAllText(source, "saved model");
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
            CheckpointFiles.CopyNew(source, target);
            Assert.Equal("saved model", File.ReadAllText(target));
            Assert.True((File.GetAttributes(source) & FileAttributes.ReadOnly) != 0);
            Assert.False((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0);
        }

        [Fact]
        public void Copy_never_overwrites_or_deletes_an_existing_target()
        {
            var source = Path.Combine(directory, "source.rvt");
            var target = Path.Combine(directory, "checkpoint.rvt");
            File.WriteAllText(source, "new");
            File.WriteAllText(target, "existing");
            Assert.ThrowsAny<IOException>(() => CheckpointFiles.CopyNew(source, target));
            Assert.Equal("existing", File.ReadAllText(target));
            Assert.Equal("new", File.ReadAllText(source));
        }
    }
}
