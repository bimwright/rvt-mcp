using RvtMcp.Plugin.Views.Settings;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class AboutInfoTests
    {
        [Fact]
        public void CommitVersionUsesExactCommitReference()
        {
            Assert.Equal(
                "https://github.com/bimwright/rvt-mcp/blob/abc1234/LICENSE",
                AboutInfoProvider.CreateLicenseSourceUrl("0.6.4+abc1234"));
        }

        [Fact]
        public void ReleaseVersionUsesVersionTagReference()
        {
            Assert.Equal(
                "https://github.com/bimwright/rvt-mcp/blob/v0.6.4/LICENSE",
                AboutInfoProvider.CreateLicenseSourceUrl("0.6.4"));
        }

        [Fact]
        public void AboutInfoUsesHostYearAndProductMetadata()
        {
            var info = AboutInfoProvider.Create(typeof(AboutInfoTests).Assembly, "2027");
            Assert.Equal("rvt-mcp", info.ProductName);
            Assert.Equal("2027", info.RevitYear);
            Assert.Equal("Apache-2.0", info.LicenseId);
            Assert.Contains("/issues", info.IssuesUrl);
        }
    }
}
