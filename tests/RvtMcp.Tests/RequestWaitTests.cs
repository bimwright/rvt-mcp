using System;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
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

        [Fact]
        public async Task Started_timeout_does_not_complete_execution_or_discard_late_result()
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lifecycle = PendingRequestLifecycle.For(tcs);
            lifecycle.Configure("req-running", Guid.NewGuid().ToString("N"), null);
            Assert.True(lifecycle.TryStart());
            var pending = JObject.Parse(RequestWait.WaitOrTimeout(tcs, "req-running", TimeSpan.FromMilliseconds(20)));
            Assert.Equal("timed_out_running", pending["_operation"]["execution"]);
            Assert.False(tcs.Task.IsCompleted);
            tcs.SetResult("late committed result");
            Assert.Equal("late committed result", await tcs.Task);
        }

        [Fact]
        public async Task Queued_timeout_and_start_are_an_atomic_choice()
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var tcs = new TaskCompletionSource<string>();
                var lifecycle = PendingRequestLifecycle.For(tcs);
                using var barrier = new Barrier(2);
                var start = Task.Run(() => { barrier.SignalAndWait(); return lifecycle.TryStart(); });
                barrier.SignalAndWait();
                var response = JObject.Parse(lifecycle.Timeout());
                if (await start)
                {
                    Assert.False(tcs.Task.IsCompleted);
                    Assert.Equal("unknown", response["_operation"]["outcome"]);
                    tcs.TrySetResult("completed");
                }
                else
                {
                    Assert.True(tcs.Task.IsCompleted);
                    Assert.Equal("not_started", response["_operation"]["outcome"]);
                }
                Assert.False(lifecycle.TryStart());
            }
        }

        [Fact]
        public async Task Dialog_event_returns_promptly_without_completing_execution()
        {
            var tcs = new TaskCompletionSource<string>();
            var lifecycle = PendingRequestLifecycle.For(tcs);
            string frame = null;
            lifecycle.Configure("dialog", Guid.NewGuid().ToString("N"), json => frame = json);
            Assert.True(lifecycle.TryStart());
            lifecycle.EmitDialogWait(new JObject { ["dialogId"] = "manual" });
            var caller = await lifecycle.CallerTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(tcs.Task.IsCompleted);
            Assert.Equal("event", JObject.Parse(frame)["frame"]);
            using (var host = System.Diagnostics.Process.GetCurrentProcess())
                Assert.Equal(host.Id + "-" + host.StartTime.ToUniversalTime().Ticks, JObject.Parse(frame).Value<string>("hostInstanceId"));
            Assert.Equal("waiting_for_user", JObject.Parse(caller)["_operation"]["execution"]);
            Assert.True(PendingRequestLifecycle.IsHostWaitingForUser);
            tcs.TrySetResult("finished");
            Assert.False(PendingRequestLifecycle.IsHostWaitingForUser);
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
