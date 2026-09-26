using System;
using System.Threading.Tasks;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class RequestWaitTests
    {
        [Fact]
        public async Task WaitOrTimeout_completes_tcs_on_timeout()
        {
            var tcs = new TaskCompletionSource<string>();
            var json = RequestWait.WaitOrTimeout(tcs, "req-1", TimeSpan.FromMilliseconds(40));

            Assert.True(tcs.Task.IsCompleted);
            var completed = await tcs.Task;
            Assert.Equal(json, completed);
            Assert.Contains("req-1", json);
            Assert.Contains("timed out", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not retry", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void WaitOrTimeout_returns_result_when_completed_in_time()
        {
            var tcs = new TaskCompletionSource<string>();
            tcs.TrySetResult("{\"id\":\"req-2\",\"success\":true}");
            var json = RequestWait.WaitOrTimeout(tcs, "req-2", TimeSpan.FromSeconds(1));
            Assert.Equal("{\"id\":\"req-2\",\"success\":true}", json);
        }

        // The plugin is the last line of defense for envelope timeouts: an
        // absent or unusable value falls back to 60s, and no envelope value -
        // including a negative or a huge one from a buggy or hostile server -
        // may wait past the 900s cap.
        [Theory]
        [InlineData(null, 60)]
        [InlineData(0, 60)]
        [InlineData(-5, 60)]
        [InlineData(1, 1)]
        [InlineData(600, 600)]
        [InlineData(900, 900)]
        [InlineData(901, 900)]
        [InlineData(int.MaxValue / 1000, 900)]
        public void EffectiveTimeout_clamps_to_defaults_and_cap(int? seconds, int expectedSeconds)
        {
            var timeout = seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null;
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RequestWait.EffectiveTimeout(timeout));
        }
    }
}
