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

        [Fact]
        public void PruneGenerated_drops_old_then_oldest_generated_captures_and_nothing_else()
        {
            var folder = Path.Combine(Path.GetTempPath(), "rvtmcp-capture-prune-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            var now = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
            string Make(string name, int ageDays, int bytes)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllBytes(path, new byte[bytes]);
                File.SetLastWriteTimeUtc(path, now.AddDays(-ageDays));
                return path;
            }
            try
            {
                var expired = Make("capture-20261001-101010-0123abcd - 3D View - A.png", 40, 10);
                var oldest = Make("capture-20261101-101010-0123abcd.png", 20, 400);
                var middle = Make("capture-20261110-101010-0123abcd.jpg", 11, 400);
                var newest = Make("capture-20261120-101010-0123abcd.png", 1, 400);
                var named = Make("before-fix - 3D View - A.png", 90, 400);
                var lookalike = Make("capture-notes.png", 90, 400);
                var nested = Make(Path.Combine("sub", "capture-20261001-101010-0123abcd.png"), 90, 400);

                // Budget 900: the expired file goes by age, then the oldest until the rest fits.
                Assert.Equal(2, CaptureOutputPath.PruneGenerated(folder, newest, now, days: 30, budget: 900));
                Assert.False(File.Exists(expired));
                Assert.False(File.Exists(oldest));
                foreach (var kept in new[] { middle, newest, named, lookalike, nested }) Assert.True(File.Exists(kept), kept);

                // The file just written survives even when it alone exceeds the budget.
                Assert.Equal(1, CaptureOutputPath.PruneGenerated(folder, newest, now, days: 30, budget: 100));
                Assert.True(File.Exists(newest));
                Assert.False(File.Exists(middle));

                Assert.Equal(0, CaptureOutputPath.PruneGenerated(Path.Combine(folder, "missing"), null, now));
            }
            finally { Directory.Delete(folder, true); }
        }

        [Fact]
        public void Validate_path_under_an_extra_root_is_accepted_and_a_sibling_folder_is_not()
        {
            var roots = new[] { @"Q:\Out" };
            Assert.Null(CaptureOutputPath.Validate(@"Q:\Out\views\plan.png", roots));

            var err = CaptureOutputPath.Validate(@"Q:\OutEvil\plan.png", roots);
            Assert.Contains("must be inside %TEMP%", err);
        }

        [Fact]
        public void Validate_extra_root_keeps_the_traversal_and_canonical_checks()
        {
            var roots = new[] { @"Q:\Out" };
            Assert.Contains("cannot contain '..'", CaptureOutputPath.Validate(@"Q:\Out\..\Windows\plan.png", roots));
            Assert.Contains("must be canonical", CaptureOutputPath.Validate(@"Q:\Out\\plan.png", roots));
        }

        [Fact]
        public void Validate_rejection_lists_every_allowed_folder_and_names_the_setting()
        {
            var err = CaptureOutputPath.Validate(@"Q:\Elsewhere\plan.png", new[] { @"Q:\Out" });
            Assert.Contains(PathAllowlist.TempDirectory, err);
            Assert.Contains(PathAllowlist.CapturesDirectory, err);
            Assert.Contains(@"Q:\Out", err);
            Assert.Contains("Omit output_path", err);
            Assert.Contains("captureRoots", err);
            Assert.Contains(RvtMcpConfig.EnvCaptureRoots, err);
        }

        [Fact]
        public void CaptureRoots_reads_an_array_or_a_semicolon_string_and_drops_what_is_not_an_absolute_local_folder()
        {
            var entries = new Newtonsoft.Json.Linq.JArray(
                @"Q:\Out", @"relative\folder", "Q:drive-relative", @"\\server\share", @"%NOT_A_REAL_VARIABLE%\x", 7, @"q:\out");
            Assert.Equal(new[] { @"Q:\Out" }, RvtMcpConfig.ReadCaptureRoots(entries));

            var fromText = RvtMcpConfig.ReadCaptureRoots(new Newtonsoft.Json.Linq.JValue(@" Q:\Out ; R:\Shots ;; "));
            Assert.Equal(new[] { @"Q:\Out", @"R:\Shots" }, fromText);

            Assert.Empty(RvtMcpConfig.ReadCaptureRoots(null));
            Assert.Empty(RvtMcpConfig.ReadCaptureRoots(new Newtonsoft.Json.Linq.JObject()));
        }

        [Fact]
        public void CaptureRoots_come_from_the_config_file_and_the_environment_overrides_it()
        {
            var folder = Path.Combine(Path.GetTempPath(), "rvtmcp-capture-roots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "config.json");
                File.WriteAllText(file, new Newtonsoft.Json.Linq.JObject
                {
                    ["captureRoots"] = new Newtonsoft.Json.Linq.JArray(@"Q:\Out")
                }.ToString());

                Assert.Equal(new[] { @"Q:\Out" }, RvtMcpConfig.LoadReadOnly(file, _ => null).CaptureRootsOrEmpty);
                Assert.Equal(new[] { @"R:\Shots" }, RvtMcpConfig.LoadReadOnly(file,
                    name => name == RvtMcpConfig.EnvCaptureRoots ? @"R:\Shots" : null).CaptureRootsOrEmpty);
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
