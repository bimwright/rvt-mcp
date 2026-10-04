using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Long-run tools accept timeout_seconds 1-900 (default 600).
    /// Out-of-range values are refused with an explicit error instead of
    /// being silently clamped, so a wrong unit surfaces immediately.
    /// </summary>
    public class TimeoutValidationTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-60000)]
        [InlineData(901)]
        [InlineData(600000)]
        [InlineData(int.MaxValue)]
        public void ValidateTimeoutSeconds_rejects_out_of_range(int timeout)
        {
            var error = ToolGateway.ValidateTimeoutSeconds(timeout);
            Assert.NotNull(error);
            Assert.Contains("1 and 900", error);
            Assert.Contains(timeout.ToString(), error);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(60)]
        [InlineData(600)]
        [InlineData(900)]
        public void ValidateTimeoutSeconds_accepts_valid_range(int timeout)
        {
            Assert.Null(ToolGateway.ValidateTimeoutSeconds(timeout));
        }
    }
}
