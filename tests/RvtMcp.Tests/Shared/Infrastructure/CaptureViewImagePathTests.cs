using System;
using System.IO;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class CaptureViewImagePathTests
    {
        [Fact]
        public void NormalizeOrDefault_NullOrEmpty_ReturnsPathUnderCapturesWithCorrectExtension()
        {
            var pngPath = CaptureOutputPath.NormalizeOrDefault(null, "png");
            Assert.StartsWith(PathAllowlist.CapturesDirectory, pngPath);
            Assert.EndsWith(".png", pngPath);
            Assert.Contains("capture-", pngPath);

            var jpegPath = CaptureOutputPath.NormalizeOrDefault("", "jpeg");
            Assert.StartsWith(PathAllowlist.CapturesDirectory, jpegPath);
            Assert.EndsWith(".jpg", jpegPath);
        }

        [Fact]
        public void NormalizeOrDefault_CustomPath_PreservesIt()
        {
            var custom = @"C:\Temp\myimage.png";
            var normalized = CaptureOutputPath.NormalizeOrDefault(custom, "png");
            Assert.Equal(custom, normalized);
        }

        [Fact]
        public void Validate_UnexpandedEnvVars_ReturnsInstructiveError()
        {
            var err1 = CaptureOutputPath.Validate(@"%TEMP%\test.png");
            Assert.Contains("unexpanded environment variables", err1);
            Assert.Contains(PathAllowlist.TempDirectory, err1);

            var err2 = CaptureOutputPath.Validate(@"%LOCALAPPDATA%\Bimwright\rvt-mcp\captures\test.png");
            Assert.Contains("unexpanded environment variables", err2);
            Assert.Contains(PathAllowlist.CapturesDirectory, err2);

            var err3 = CaptureOutputPath.Validate(@"%temp%\test.png");
            Assert.Contains("unexpanded environment variables", err3);

            var err4 = CaptureOutputPath.Validate(@"%LocalAppData%\Bimwright\rvt-mcp\captures\test.png");
            Assert.Contains("unexpanded environment variables", err4);
        }

        [Fact]
        public void Validate_UNCPath_Rejected()
        {
            var err1 = CaptureOutputPath.Validate(@"\\server\share\test.png");
            Assert.Equal("UNC paths are not allowed.", err1);

            var err2 = CaptureOutputPath.Validate(@"//server/share/test.png");
            Assert.Equal("UNC paths are not allowed.", err2);
        }

        [Fact]
        public void Validate_ParentDirectoryTraversal_Rejected()
        {
            var err = CaptureOutputPath.Validate(Path.Combine(PathAllowlist.TempDirectory, @"..\test.png"));
            Assert.Equal("output_path cannot contain '..'.", err);
        }

        [Fact]
        public void Validate_NonCanonicalPath_Rejected()
        {
            // E.g. lowercase drive letter or extra slashes on Windows
            var nonCanonical = PathAllowlist.TempDirectory.ToLowerInvariant() + @"\\test.png";
            var err = CaptureOutputPath.Validate(nonCanonical);
            Assert.Contains("must be canonical", err);
        }

        [Fact]
        public void Validate_OutsideAllowlist_Rejected()
        {
            var outside = @"C:\Windows\System32\test.png";
            var err = CaptureOutputPath.Validate(outside);
            Assert.Contains("must be inside %TEMP%", err);
            Assert.Contains(PathAllowlist.TempDirectory, err);
            Assert.Contains(PathAllowlist.CapturesDirectory, err);
        }

        [Fact]
        public void Validate_ValidPath_ReturnsNull()
        {
            var validTemp = Path.Combine(PathAllowlist.TempDirectory, "test.png");
            Assert.Null(CaptureOutputPath.Validate(validTemp));

            var validCaptures = Path.Combine(PathAllowlist.CapturesDirectory, "test.jpg");
            Assert.Null(CaptureOutputPath.Validate(validCaptures));
        }
    }
}
