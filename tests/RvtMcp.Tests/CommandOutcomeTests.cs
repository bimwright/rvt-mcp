using System;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CommandOutcomeTests
    {
        // Same shape BatchExecuteHandler returns: new { results = outcome.Results, rolledBack }.
        private static object BatchData(bool continueOnError, params string[] commands)
        {
            var arr = new JArray();
            foreach (var name in commands)
                arr.Add(new JObject { ["command"] = name });

            var outcome = BatchExecutor.Run(arr, continueOnError, (name, _) =>
                name == "bad"
                    ? BatchExecutor.InvokeResult.Fail("intentional failure")
                    : name == "partial"
                        ? BatchExecutor.InvokeResult.Ok(new { failedCount = 2 })
                        : BatchExecutor.InvokeResult.Ok(new { elementId = 1 }));
            var rolledBack = outcome.AnyFailed && !continueOnError;
            return new { results = outcome.Results, rolledBack };
        }

        [Fact]
        public void Plain_success_passes_through()
        {
            var o = CommandOutcome.Normalize("create_level", true, new { elementId = 1 }, null, null);

            Assert.True(o.Success);
            Assert.Null(o.Error);
        }

        [Fact]
        public void Handler_failure_keeps_its_error()
        {
            var o = CommandOutcome.Normalize("create_level", false, null, "No document is open.", null);

            Assert.False(o.Success);
            Assert.Equal("No document is open.", o.Error);
        }

        [Fact]
        public void Rejected_oversized_response_is_a_failure_with_the_agent_error()
        {
            var o = CommandOutcome.Normalize("get_schedule_data", true, new { rows = 1 }, null,
                "Response too large.");

            Assert.False(o.Success);
            Assert.Equal("Response too large.", o.Error);
        }

        [Fact]
        public void Rejected_error_wins_over_a_batch_failure()
        {
            var o = CommandOutcome.Normalize("batch_execute", true, BatchData(false, "ok", "bad"), null,
                "Response too large.");

            Assert.False(o.Success);
            Assert.Equal("Response too large.", o.Error);
        }

        [Fact]
        public void Rolled_back_batch_is_a_failure()
        {
            var o = CommandOutcome.Normalize("batch_execute", true, BatchData(false, "ok", "bad", "ok"), null, null);

            Assert.False(o.Success);
            Assert.Equal("Batch rolled back: 1 sub-command(s) failed, no changes kept.", o.Error);
        }

        [Fact]
        public void ContinueOnError_batch_with_failed_sub_commands_is_a_failure()
        {
            var o = CommandOutcome.Normalize("batch_execute", true, BatchData(true, "ok", "bad", "partial"), null, null);

            Assert.False(o.Success);
            Assert.Equal("2 of 3 batch sub-command(s) failed.", o.Error);
        }

        [Fact]
        public void Batch_with_all_sub_commands_ok_is_a_success()
        {
            var o = CommandOutcome.Normalize("batch_execute", true, BatchData(true, "ok", "ok"), null, null);

            Assert.True(o.Success);
            Assert.Null(o.Error);
        }

        [Fact]
        public void Batch_handler_failure_keeps_its_error()
        {
            var o = CommandOutcome.Normalize("batch_execute", false, null, "No document is open.", null);

            Assert.False(o.Success);
            Assert.Equal("No document is open.", o.Error);
        }

        [Fact]
        public void Batch_shape_is_only_read_for_batch_execute()
        {
            var o = CommandOutcome.Normalize("send_code_to_revit", true,
                new { rolledBack = true, results = new[] { new { ok = false } } }, null, null);

            Assert.True(o.Success);
        }

        [Fact]
        public void BatchExecuteHandler_still_returns_the_shape_the_outcome_reads()
        {
            var source = ReadSource("Handlers", "BatchExecuteHandler.cs");

            Assert.Contains("new { results = outcome.Results, rolledBack }", source);
        }

        [Fact]
        public void McpEventHandler_reports_one_outcome_after_the_size_guard()
        {
            var source = ReadSource("Infrastructure", "McpEventHandler.cs");

            var guard = source.IndexOf("ResponseEnvelopeGuard.Apply(", StringComparison.Ordinal);
            var normalize = source.IndexOf("CommandOutcome.Normalize(", StringComparison.Ordinal);
            var fileLog = source.IndexOf("McpLogger.Log(request.CommandName, request.ParamsJson, outcome.Success", StringComparison.Ordinal);
            var sessionLog = source.IndexOf("Success = outcome.Success", StringComparison.Ordinal);

            Assert.True(guard >= 0 && normalize > guard, "outcome must be judged after the size guard");
            Assert.True(fileLog > normalize, "mcp-calls.jsonl (History of past sessions) must log the outcome");
            Assert.True(sessionLog > normalize, "History must record the outcome");
            Assert.DoesNotContain("Success = result.Success", source);

            var toast = source.IndexOf("_toastNotifier?.OnCompleted(", sessionLog, StringComparison.Ordinal);
            Assert.True(toast > sessionLog, "toast call must follow the session log on the success path");
            var toastArgs = source.Substring(toast, source.IndexOf(");", toast, StringComparison.Ordinal) - toast);
            Assert.Contains("outcome.Success", toastArgs);
            Assert.Contains("outcome.Error", toastArgs);
        }

        private static string ReadSource(string folder, string file, [CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
            return File.ReadAllText(Path.Combine(root, "src", "shared", folder, file));
        }
    }
}
