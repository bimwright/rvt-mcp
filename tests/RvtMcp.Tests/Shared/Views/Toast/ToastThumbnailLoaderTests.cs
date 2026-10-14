using System;
using System.IO;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToastThumbnailLoaderTests : IDisposable
    {
        private readonly string _tempFile;

        public ToastThumbnailLoaderTests()
        {
            _tempFile = Path.GetTempFileName();
        }

        public void Dispose()
        {
            if (File.Exists(_tempFile))
            {
                try { File.Delete(_tempFile); } catch { }
            }
        }

        [Fact]
        public void TryLoadBytes_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(ToastThumbnailLoader.TryLoadBytes(null, 100));
            Assert.Null(ToastThumbnailLoader.TryLoadBytes("", 100));
        }

        [Fact]
        public void TryLoadBytes_FileDoesNotExist_ReturnsNull()
        {
            Assert.Null(ToastThumbnailLoader.TryLoadBytes(Path.Combine(Path.GetTempPath(), "nonexistent-file.png"), 100));
        }

        [Fact]
        public void TryLoadBytes_FileSizeExceedsMax_ReturnsNull()
        {
            var data = new byte[200];
            File.WriteAllBytes(_tempFile, data);

            Assert.Null(ToastThumbnailLoader.TryLoadBytes(_tempFile, 100));
        }

        [Fact]
        public void TryLoadBytes_FileSizeWithinMax_ReturnsBytes()
        {
            var data = new byte[50];
            new Random().NextBytes(data);
            File.WriteAllBytes(_tempFile, data);

            var loaded = ToastThumbnailLoader.TryLoadBytes(_tempFile, 100);
            Assert.NotNull(loaded);
            Assert.Equal(data, loaded);
        }
    }
}
