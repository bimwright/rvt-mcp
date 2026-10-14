using System;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Separates the caller wait from execution completion so the UI-thread
    /// handler skips a timed-out queued command but retains every started operation result.
    /// Long-run commands pass a caller-supplied wait (envelope timeout_seconds);
    /// it is clamped to MaxTimeout defensively.
    /// </summary>
    public static class RequestWait
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(900);

        /// <summary>
        /// The wait actually applied for a request: the caller's value when it
        /// is usable, the 60s default when absent or non-positive, and a
        /// defensive cap at MaxTimeout regardless of what the envelope carried.
        /// </summary>
        internal static TimeSpan EffectiveTimeout(TimeSpan? timeout)
        {
            var wait = timeout ?? DefaultTimeout;
            if (wait <= TimeSpan.Zero) wait = DefaultTimeout;
            if (wait > MaxTimeout) wait = MaxTimeout;
            return wait;
        }

        public static string WaitOrTimeout(TaskCompletionSource<string> tcs, string id, TimeSpan? timeout = null)
        {
            if (tcs == null) throw new ArgumentNullException(nameof(tcs));

            var lifecycle = PendingRequestLifecycle.For(tcs);
            if (lifecycle.RequestId == null) lifecycle.Configure(id, null, null);
            return lifecycle.CallerTask.Wait(EffectiveTimeout(timeout))
                ? lifecycle.CallerTask.GetAwaiter().GetResult() : lifecycle.Timeout();
        }
    }
}
