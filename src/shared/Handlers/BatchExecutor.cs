using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// A6 batch iteration logic, factored out of <see cref="BatchExecuteHandler"/> so it
    /// can be unit-tested without a live Revit document or TransactionGroup. The handler
    /// still owns the TransactionGroup Start/Assimilate/RollBack calls and decides what
    /// to do based on <see cref="Outcome.AnyFailed"/>.
    /// </summary>
    public static class BatchExecutor
    {
        public class Outcome
        {
            public List<object> Results { get; set; } = new List<object>();
            public bool AnyFailed { get; set; }
        }

        public const int MaxCommands = 20;

        public static string TooManyCommandsMessage(int count) =>
            $"batch_execute supports at most {MaxCommands} commands (got {count}). Split the batch.";

        /// <param name="commandsArr">The <c>commands</c> JArray from the request params.</param>
        /// <param name="continueOnError">If false, stops at first failure.</param>
        /// <param name="invoke">
        /// Called for each valid sub-command. Receives command name + params JSON, returns
        /// (success, dataOrError). Implementations wrap the real dispatcher in the handler;
        /// tests wrap a dictionary-backed stub.
        /// </param>
        /// <param name="isBakedCommand">
        /// Optional predicate used by the Revit handler to keep baked tools out of
        /// batch_execute; baked tools must run through run_baked_tool.
        /// </param>
        public static Outcome Run(
            JArray commandsArr,
            bool continueOnError,
            Func<string, string, InvokeResult> invoke,
            Func<string, bool> isBakedCommand = null)
        {
            if (commandsArr == null) throw new ArgumentNullException(nameof(commandsArr));
            if (invoke == null) throw new ArgumentNullException(nameof(invoke));

            var outcome = new Outcome();

            if (commandsArr.Count > MaxCommands)
            {
                outcome.AnyFailed = true;
                outcome.Results.Add(new { index = -1, ok = false, error = TooManyCommandsMessage(commandsArr.Count) });
                return outcome;
            }

            for (var i = 0; i < commandsArr.Count; i++)
            {
                var cmd = commandsArr[i] as JObject;
                var cmdName = cmd?.Value<string>("command");

                if (string.IsNullOrEmpty(cmdName))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = "Missing 'command' field." });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                // Nested batch_execute would double-wrap the TransactionGroup — block explicitly.
                if (string.Equals(cmdName, "batch_execute", StringComparison.Ordinal))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = "Nested batch_execute is not supported." });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                if (string.Equals(cmdName, "run_baked_tool", StringComparison.Ordinal))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = RunBakedToolNotSupportedMessage() });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                // Arbitrary code must use its own MCP tool and session switch, not an
                // annotated batch tool that could bypass send_code exposure controls.
                if (string.Equals(cmdName, "send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = "send_code_to_revit cannot be invoked through batch_execute; call send_code_to_revit directly." });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                if (isBakedCommand != null && isBakedCommand(cmdName))
                {
                    outcome.Results.Add(new { index = i, ok = false, error = BakedCommandNotSupportedMessage(cmdName) });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                var subParams = cmd["params"]?.ToString() ?? "{}";
                InvokeResult r;
                try
                {
                    r = invoke(cmdName, subParams);
                }
                catch (Exception ex)
                {
                    outcome.Results.Add(new { index = i, ok = false, error = ex.Message });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                if (r.UnknownCommand)
                {
                    outcome.Results.Add(new { index = i, ok = false, error = $"Unknown command: {cmdName}" });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                    continue;
                }

                if (r.Success)
                {
                    if (TryGetPartialFailureCount(r.Data, out var failedCount))
                    {
                        outcome.Results.Add(new
                        {
                            index = i,
                            ok = false,
                            error = $"Command completed with {failedCount} partial failure(s).",
                            data = r.Data
                        });
                        outcome.AnyFailed = true;
                        if (!continueOnError) return outcome;
                        continue;
                    }

                    outcome.Results.Add(new { index = i, ok = true, data = r.Data });
                }
                else
                {
                    outcome.Results.Add(new { index = i, ok = false, error = r.Error });
                    outcome.AnyFailed = true;
                    if (!continueOnError) return outcome;
                }
            }

            return outcome;
        }

        public static string BakedCommandNotSupportedMessage(string name) =>
            $"Baked tool '{name}' cannot be run through batch_execute. Use run_baked_tool instead.";

        public static string RunBakedToolNotSupportedMessage() =>
            "run_baked_tool cannot be invoked through batch_execute; call run_baked_tool directly.";

        private static bool TryGetPartialFailureCount(object data, out int failedCount)
        {
            failedCount = 0;
            if (data == null)
                return false;

            try
            {
                var obj = data as JObject ?? JObject.FromObject(data);
                var token = obj["failedCount"];
                if (token == null || token.Type != JTokenType.Integer)
                    return false;

                failedCount = token.Value<int>();
                return failedCount > 0;
            }
            catch
            {
                failedCount = 0;
                return false;
            }
        }

        public class InvokeResult
        {
            public bool Success { get; set; }
            public object Data { get; set; }
            public string Error { get; set; }
            public bool UnknownCommand { get; set; }

            public static InvokeResult Ok(object data) =>
                new InvokeResult { Success = true, Data = data };

            public static InvokeResult Fail(string error) =>
                new InvokeResult { Success = false, Error = error };

            public static InvokeResult Unknown() =>
                new InvokeResult { UnknownCommand = true };
        }
    }
}
