using System;
using System.Collections.Generic;
using Bimwright.Rvt.Plugin.Handlers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bimwright.Rvt.Tests
{
    public class BatchExecutorTests
    {
        // Scripted dispatcher: dict of commandName → (params → InvokeResult)
        private static Func<string, string, BatchExecutor.InvokeResult> Dispatch(
            Dictionary<string, Func<string, BatchExecutor.InvokeResult>> scripts) =>
            (name, pj) => scripts.TryGetValue(name, out var f)
                ? f(pj)
                : BatchExecutor.InvokeResult.Unknown();

        private static JArray Cmds(params (string name, object prms)[] cmds)
        {
            var arr = new JArray();
            foreach (var (name, prms) in cmds)
            {
                arr.Add(new JObject
                {
                    ["command"] = name,
                    ["params"] = prms == null ? null : JToken.FromObject(prms),
                });
            }
            return arr;
        }

        // --- P3-007 required cases -----------------------------------------

        [Fact]
        public void Run_ThreeCommandsAllSucceed_AssimilatePath()
        {
            // Happy path: 3/3 succeed → AnyFailed=false → caller calls Assimilate
            var dispatch = Dispatch(new Dictionary<string, Func<string, BatchExecutor.InvokeResult>>
            {
                ["create_level"] = _ => BatchExecutor.InvokeResult.Ok(new { elementId = 1 }),
                ["create_grid"]  = _ => BatchExecutor.InvokeResult.Ok(new { elementId = 2 }),
            });

            var cmds = Cmds(
                ("create_level", new { elevation = 3000 }),
                ("create_level", new { elevation = 6000 }),
                ("create_grid",  new { startX = 0, startY = 0, endX = 5000, endY = 0 }));

            var outcome = BatchExecutor.Run(cmds, continueOnError: false, dispatch);

            Assert.False(outcome.AnyFailed);
            Assert.Equal(3, outcome.Results.Count);
        }

        [Fact]
        public void Run_MiddleCommandFails_StopsAndFlags_ForRollback()
        {
            // Middle fails + continueOnError=false → stops at index 1, outcome.AnyFailed = true.
            // Caller interprets AnyFailed && !continueOnError → RollBack the TransactionGroup.
            var dispatch = Dispatch(new Dictionary<string, Func<string, BatchExecutor.InvokeResult>>
            {
                ["create_level"] = _ => BatchExecutor.InvokeResult.Ok(new { elementId = 1 }),
                ["bad_command"]  = _ => BatchExecutor.InvokeResult.Fail("intentional failure"),
                ["create_grid"]  = _ => BatchExecutor.InvokeResult.Ok(new { elementId = 3 }),
            });

            var cmds = Cmds(
                ("create_level", null),
                ("bad_command",  null),
                ("create_grid",  null));

            var outcome = BatchExecutor.Run(cmds, continueOnError: false, dispatch);

            Assert.True(outcome.AnyFailed);
            Assert.Equal(2, outcome.Results.Count); // third command not attempted
        }

        [Fact]
        public void Run_ContinueOnErrorTrue_AllCommandsAttempted()
        {
            // continueOnError=true: every sub-command runs, per-command ok/error recorded.
            // Caller sees AnyFailed=true but continueOnError → Assimilate (keep what succeeded).
            var dispatch = Dispatch(new Dictionary<string, Func<string, BatchExecutor.InvokeResult>>
            {
                ["create_level"] = _ => BatchExecutor.InvokeResult.Ok(new { elementId = 1 }),
                ["bad_command"]  = _ => BatchExecutor.InvokeResult.Fail("intentional failure"),
                ["create_grid"]  = _ => BatchExecutor.InvokeResult.Ok(new { elementId = 3 }),
            });

            var cmds = Cmds(
                ("create_level", null),
                ("bad_command",  null),
                ("create_grid",  null));

            var outcome = BatchExecutor.Run(cmds, continueOnError: true, dispatch);

            Assert.True(outcome.AnyFailed);
            Assert.Equal(3, outcome.Results.Count);
        }

        // --- Edge / guard cases --------------------------------------------

        [Fact]
        public void Run_MissingCommandField_FailsThatIndex()
        {
            var cmds = new JArray
            {
                new JObject { ["params"] = new JObject() }, // no 'command' key
            };

            var outcome = BatchExecutor.Run(cmds, continueOnError: false,
                (_, __) => BatchExecutor.InvokeResult.Ok(null));

            Assert.True(outcome.AnyFailed);
            Assert.Single(outcome.Results);
        }

        [Fact]
        public void Run_UnknownCommand_FailsThatIndex()
        {
            var dispatch = Dispatch(new Dictionary<string, Func<string, BatchExecutor.InvokeResult>>());
            var cmds = Cmds(("never_registered", null));

            var outcome = BatchExecutor.Run(cmds, continueOnError: false, dispatch);

            Assert.True(outcome.AnyFailed);
        }

        [Fact]
        public void Run_NestedBatchExecute_Rejected()
        {
            var cmds = Cmds(("batch_execute", new { commands = new object[0] }));

            var outcome = BatchExecutor.Run(cmds, continueOnError: false,
                (_, __) => BatchExecutor.InvokeResult.Ok(null));

            Assert.True(outcome.AnyFailed);
        }

        [Fact]
        public void Run_HandlerThrows_CapturedAsFailure()
        {
            var dispatch = Dispatch(new Dictionary<string, Func<string, BatchExecutor.InvokeResult>>
            {
                ["boom"] = _ => throw new InvalidOperationException("kaboom"),
            });

            var cmds = Cmds(("boom", null));

            var outcome = BatchExecutor.Run(cmds, continueOnError: false, dispatch);

            Assert.True(outcome.AnyFailed);
            Assert.Single(outcome.Results);
        }

        [Fact]
        public void Run_NullCommandsArray_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                BatchExecutor.Run(null, false, (_, __) => BatchExecutor.InvokeResult.Ok(null)));
        }

        [Fact]
        public void Run_NullInvoke_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                BatchExecutor.Run(new JArray(), false, null));
        }
    }
}
