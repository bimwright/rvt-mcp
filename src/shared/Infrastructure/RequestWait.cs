using System;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Completes the per-request TCS when the 60s wait expires so the UI-thread
    /// handler can skip a stale command instead of writing a late success.
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

            var wait = EffectiveTimeout(timeout);
            if (tcs.Task.Wait(wait))
                return tcs.Task.Result;

            var response = JsonConvert.SerializeObject(new
            {
                id,
                success = false,
                error = $"Request timed out ({Math.Max(1, (int)wait.TotalSeconds)}s). Revit may still be running this command. " +
                        "Do not retry clash, export, or other long tools until the current Revit operation finishes."
            });
            tcs.TrySetResult(response);
            return response;
        }
    }
}
