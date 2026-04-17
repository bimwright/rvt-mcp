using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
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

        /// <param name="commandsArr">The <c>commands</c> JArray from the request params.</param>
        /// <param name="continueOnError">If false, stops at first failure.</param>
        /// <param name="invoke">
        /// Called for each valid sub-command. Receives command name + params JSON, returns
        /// (success, dataOrError). Implementations wrap the real dispatcher in the handler;
        /// tests wrap a dictionary-backed stub.
        /// </param>
        public static Outcome Run(
            JArray commandsArr,
            bool continueOnError,
            Func<string, string, InvokeResult> invoke)
        {
            if (commandsArr == null) throw new ArgumentNullException(nameof(commandsArr));
            if (invoke == null) throw new ArgumentNullException(nameof(invoke));

            var outcome = new Outcome();

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
