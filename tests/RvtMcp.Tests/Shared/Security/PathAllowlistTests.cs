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
