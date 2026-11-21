using System.IO;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class PathAllowlistTests
    {
        [Fact]
        public void IsUnderRoot_AcceptsFileInsideRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvtmcp-allow-" + Path.GetRandomFileName());
            Directory.CreateDirectory(root);
            var file = Path.Combine(root, "a.png");
            File.WriteAllText(file, "x");
            try
            {
                Assert.True(PathAllowlist.IsUnderRoot(file, root));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void UserTemp_is_the_user_level_folder_and_falls_back_to_the_process_one()
        {
            // Revit runs with TEMP pointing at a per-session subfolder of the user's temp folder.
            Assert.Equal(@"Q:\Temp", PathAllowlist.UserTemp(@"Q:\Temp\session", @"Q:\Temp"));
            Assert.Equal(@"Q:\Temp\session", PathAllowlist.UserTemp(@"Q:\Temp\session", null));
            Assert.Equal(@"Q:\Temp\session", PathAllowlist.UserTemp(@"Q:\Temp\session", @"relative\temp"));
            Assert.Equal(@"Q:\Temp\session", PathAllowlist.UserTemp(@"Q:\Temp\session", @"%NOT_A_REAL_VARIABLE%\Temp"));
        }

        [Fact]
        public void Capture_allowed_roots_list_the_temp_and_captures_folders_once()
        {
            var roots = CaptureOutputPath.AllowedRoots();
            Assert.Contains(PathAllowlist.TempDirectory, roots);
            Assert.Contains(PathAllowlist.CapturesDirectory, roots);
            Assert.Equal(roots.Length, new System.Collections.Generic.HashSet<string>(roots, System.StringComparer.OrdinalIgnoreCase).Count);
            Assert.Null(CaptureOutputPath.Validate(Path.Combine(PathAllowlist.UserTempDirectory, "view.png")));
        }

        [Fact]
        public void IsUnderRoot_RejectsSiblingDirectory()
        {
            var parent = Path.Combine(Path.GetTempPath(), "rvtmcp-allow-parent-" + Path.GetRandomFileName());
            var root = Path.Combine(parent, "captures");
            var sibling = Path.Combine(parent, "capturesEvil");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);
            var file = Path.Combine(sibling, "a.png");
            File.WriteAllText(file, "x");
            try
            {
                Assert.False(PathAllowlist.IsUnderRoot(file, root));
            }
            finally
            {
                try { Directory.Delete(parent, true); } catch { }
            }
        }
    }
}
