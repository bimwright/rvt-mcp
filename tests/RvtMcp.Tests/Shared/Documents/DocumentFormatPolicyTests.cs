using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class DocumentFormatPolicyTests
    {
        [Theory]
        [InlineData("2022", 2026, true)]
        [InlineData("2026", 2026, false)]
        [InlineData("2027", 2026, false)]
        [InlineData(null, 2026, null)]
        [InlineData("unknown", 2026, null)]
        [InlineData("100", 2026, null)]
        public void Upgrade_is_unknown_when_saved_format_has_no_valid_year(string format, int hostYear, bool? expected)
        {
            Assert.Equal(expected, DocumentFormatPolicy.RequiresUpgrade(format, hostYear));
        }
    }
}
