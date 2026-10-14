using System;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// The outcome the toast, History and mcp-calls.jsonl report for one request. It can
    /// differ from the handler's <c>CommandResult</c>:
    /// <list type="bullet">
    /// <item>a response the size guard rejects reaches the agent as <c>success=false</c>;</item>
    /// <item><c>batch_execute</c> returns Ok with per-command detail even when a sub-command
    /// failed or the TransactionGroup rolled back.</item>
    /// </list>
    /// The wire response is not changed here — only what the user-facing surfaces report.
    /// </summary>
    public sealed class CommandOutcome
    {
        public bool Success { get; }
        public string Error { get; }

        private CommandOutcome(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        /// <param name="handlerData">The handler's data before any spill replaced it.</param>
        /// <param name="rejectError">The size-guard error the agent received, or null.</param>
        public static CommandOutcome Normalize(
            string commandName,
            bool handlerSuccess,
            object handlerData,
            string handlerError,
            string rejectError)
        {
            if (rejectError != null)
                return new CommandOutcome(false, rejectError);

            if (handlerSuccess)
            {
                var batchError = BatchFailureError(commandName, handlerData);
                if (batchError != null)
                    return new CommandOutcome(false, batchError);
            }

            return new CommandOutcome(handlerSuccess, handlerError);
        }

        private static string BatchFailureError(string commandName, object data)
        {
            if (!string.Equals(commandName, "batch_execute", StringComparison.Ordinal) || data == null)
                return null;

            try
            {
                var obj = data as JObject ?? JObject.FromObject(data);
                var results = obj["results"] as JArray;
                var total = results?.Count ?? 0;
                var failed = 0;
                if (results != null)
                    foreach (var r in results)
                        if (r is JObject ro && ro.Value<bool?>("ok") == false)
                            failed++;

                if (obj.Value<bool?>("rolledBack") == true)
                    return $"Batch rolled back: {failed} sub-command(s) failed, no changes kept.";
                if (failed > 0)
                    return $"{failed} of {total} batch sub-command(s) failed.";
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
