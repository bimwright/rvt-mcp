using System;
using System.IO;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class CaptureOutputResolverTests
    {
        [Fact]
        public void FindActualOutput_picks_newest_revit_suffixed_file()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rvtmcp-capture-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var requested = Path.Combine(dir, "demo.png");
                var older = Path.Combine(dir, "demo - Sheet - A - Old.png");
                var newer = Path.Combine(dir, "demo - Sheet - P000 - COVER SHEET.png");
                File.WriteAllText(older, "a");
                File.WriteAllText(newer, "bb");
                File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
                File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

                var resolved = CaptureOutputResolver.FindActualOutput(requested, "png");

                Assert.Equal(newer, resolved);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void IsRevitExportMatch_rejects_ztest_when_base_is_z()
        {
            Assert.True(CaptureOutputResolver.IsRevitExportMatch("z - Sheet - A.png", "z", "png"));
            Assert.False(CaptureOutputResolver.IsRevitExportMatch("ztest - Sheet - A.png", "z", "png"));
        }
    }
}
