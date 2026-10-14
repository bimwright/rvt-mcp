using System;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class PersistSendCodeTtlTests
    {
        [Theory]
        [InlineData("4h", 4.0)]
        [InlineData("1h", 1.0)]
        [InlineData("2d", 48.0)]
        [InlineData("90m", 1.5)]
        public void TryParse_Valid_ReturnsExpectedHours(string input, double hours)
        {
            Assert.True(PersistSendCodeTtl.TryParse(input, out var ts));
            Assert.Equal(TimeSpan.FromHours(hours), ts);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("0h")]
        public void TryParse_Invalid_ReturnsFalse(string input)
        {
            Assert.False(PersistSendCodeTtl.TryParse(input, out _));
        }

        [Fact]
        public void Clamp_BelowMin_BecomesOneHour()
        {
            Assert.Equal(PersistSendCodeTtl.Min, PersistSendCodeTtl.Clamp(TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void Clamp_AboveMax_BecomesTwoDays()
        {
            Assert.Equal(PersistSendCodeTtl.Max, PersistSendCodeTtl.Clamp(TimeSpan.FromDays(9)));
        }
    }
}
