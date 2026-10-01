using System;
using System.Collections.Generic;
using RvtMcp.Plugin.Handlers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class BatchExecutorTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Send_code_cannot_bypass_its_own_MCP_switch_through_a_batch(bool continueOnError)
        {
            var invoked = false;
            var outcome = BatchExecutor.Run(new JArray(new JObject
            {
                ["command"] = "send_code_to_revit", ["params"] = new JObject { ["code"] = "return 1;" }
            }), continueOnError, (name, parameters) =>
            {
                invoked = true;
                return BatchExecutor.InvokeResult.Ok(new { });
            });
            Assert.False(invoked);
            Assert.True(outcome.AnyFailed);
            Assert.Contains("call send_code_to_revit directly", Newtonsoft.Json.JsonConvert.SerializeObject(outcome.Results));
        }

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

        [Fact]
        public void Run_MoreThanMaxCommands_FailsBeforeInvoke()
        {
            var invoked = 0;
            var cmds = new JArray();
            for (var i = 0; i < 21; i++)
                cmds.Add(new JObject { ["command"] = "create_grid", ["params"] = new JObject() });

            var outcome = BatchExecutor.Run(cmds, continueOnError: false, (_, __) =>
            {
                invoked++;
                return BatchExecutor.InvokeResult.Ok(new { elementId = 1 });
            });

            Assert.Equal(0, invoked);
            Assert.True(outcome.AnyFailed);
            Assert.Single(outcome.Results);
            var row = JObject.FromObject(outcome.Results[0]);
            Assert.False(row.Value<bool>("ok"));
            Assert.Contains("20", row.Value<string>("error"));
        }

        [Fact]
        public void Run_SuccessPayloadWithFailedCount_StopsAndFlags_ForRollback()
        {
            var dispatch = Dispatch(new Dictionary<string, Func<string, BatchExecutor.InvokeResult>>
            {
                ["set_element_parameter_values"] = _ => BatchExecutor.InvokeResult.Ok(new { updatedCount = 1, failedCount = 1 }),
                ["create_grid"] = _ => BatchExecutor.InvokeResult.Ok(new { elementId = 3 }),
            });

            var cmds = Cmds(
                ("set_element_parameter_values", null),
                ("create_grid", null));

            var outcome = BatchExecutor.Run(cmds, continueOnError: false, dispatch);

            Assert.True(outcome.AnyFailed);
            Assert.Single(outcome.Results);
            var result = JObject.FromObject(outcome.Results[0]);
            Assert.False(result.Value<bool>("ok"));
            Assert.Equal(1, result["data"]?.Value<int>("failedCount"));
            Assert.Contains("partial failure", result.Value<string>("error"));
        }

        [Fact]
        public void Run_SuccessPayloadWithFailedCount_ContinueOnErrorTrue_AttemptsRemaining()
        {
            var dispatch = Dispatch(new Dictionary<string, Func<string, BatchExecutor.InvokeResult>>
            {
                ["set_type_parameter_values"] = _ => BatchExecutor.InvokeResult.Ok(new JObject
                {
                    ["updatedCount"] = 1,
                    ["failedCount"] = 2
                }),
                ["create_grid"] = _ => BatchExecutor.InvokeResult.Ok(new { elementId = 3 }),
            });

            var cmds = Cmds(
                ("set_type_parameter_values", null),
                ("create_grid", null));

            var outcome = BatchExecutor.Run(cmds, continueOnError: true, dispatch);

            Assert.True(outcome.AnyFailed);
            Assert.Equal(2, outcome.Results.Count);
            Assert.False(JObject.FromObject(outcome.Results[0]).Value<bool>("ok"));
            Assert.True(JObject.FromObject(outcome.Results[1]).Value<bool>("ok"));
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
        public void Run_BakedCommandName_RejectedBeforeInvoke()
        {
            var invoked = false;
            var cmds = Cmds(("custom_baked_command", new { value = 1 }));

            var outcome = BatchExecutor.Run(
                cmds,
                continueOnError: false,
                (_, __) =>
                {
                    invoked = true;
                    return BatchExecutor.InvokeResult.Ok(null);
                },
                isBakedCommand: name => name == "custom_baked_command");

            Assert.True(outcome.AnyFailed);
            Assert.False(invoked);
            var result = JObject.FromObject(outcome.Results[0]);
            Assert.Equal("Baked tool 'custom_baked_command' cannot be run through batch_execute. Use run_baked_tool instead.", result.Value<string>("error"));
        }

        [Fact]
        public void Run_RunBakedToolCommand_RejectedBeforeInvoke()
        {
            var invoked = false;
            var cmds = Cmds(("run_baked_tool", new { name = "custom_baked_command", @params = new { value = 1 } }));

            var outcome = BatchExecutor.Run(
                cmds,
                continueOnError: false,
                (_, __) =>
                {
                    invoked = true;
                    return BatchExecutor.InvokeResult.Ok(null);
                });

            Assert.True(outcome.AnyFailed);
            Assert.False(invoked);
            var result = JObject.FromObject(outcome.Results[0]);
            Assert.Equal("run_baked_tool cannot be invoked through batch_execute; call run_baked_tool directly.", result.Value<string>("error"));
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
