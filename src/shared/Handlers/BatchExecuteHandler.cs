using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    /// <summary>
    /// A6 batch execution (aspect #3 §A6). Wraps N MCP commands in a single
    /// <see cref="TransactionGroup"/> so the whole batch resolves to one Revit undo
    /// step. Inner handlers each open their own <see cref="Transaction"/>; TransactionGroup
    /// permits that (nested Transactions are forbidden, but inner-tx-inside-tx-group is fine).
    ///
    /// Iteration + rollback-decision logic lives in <see cref="BatchExecutor"/> so it can
    /// be unit-tested without a live document. This handler owns only the TransactionGroup
    /// lifecycle + the final Assimilate/RollBack call based on the outcome.
    /// </summary>
    public class BatchExecuteHandler : IRevitCommand
    {
        private readonly CommandDispatcher _dispatcher;

        public BatchExecuteHandler(CommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public string Name => "batch_execute";
        public string Description => "Run multiple MCP commands in one Revit TransactionGroup (one undo step).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""commands"":{""type"":""array"",""items"":{""type"":""object""}},""continueOnError"":{""type"":""boolean""}},""required"":[""commands""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var commandsArr = request["commands"] as JArray;
            if (commandsArr == null || commandsArr.Count == 0)
                return CommandResult.Fail("'commands' must be a non-empty array.");

            var continueOnError = request.Value<bool?>("continueOnError") ?? false;

            using (var tg = new TransactionGroup(doc, "MCP: batch_execute"))
            {
                tg.Start();

                var outcome = BatchExecutor.Run(commandsArr, continueOnError, (cmdName, subParams) =>
                {
                    var handler = _dispatcher.GetCommand(cmdName);
                    if (handler == null) return BatchExecutor.InvokeResult.Unknown();
                    var r = handler.Execute(app, subParams);
                    return r.Success
                        ? BatchExecutor.InvokeResult.Ok(r.Data)
                        : BatchExecutor.InvokeResult.Fail(r.Error);
                });

                bool rolledBack;
                if (outcome.AnyFailed && !continueOnError)
                {
                    tg.RollBack();
                    rolledBack = true;
                }
                else
                {
                    tg.Assimilate();
                    rolledBack = false;
                }

                return CommandResult.Ok(new { results = outcome.Results, rolledBack });
            }
        }
    }
}
