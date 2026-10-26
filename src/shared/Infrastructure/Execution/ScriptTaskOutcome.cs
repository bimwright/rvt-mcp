using RvtMcp.Scripting;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Script-only task truth table. Does not commit, roll back or modify native receipts.
    /// Do not apply it to typed read-only handlers: their normal native outcome may be not_started.
    /// </summary>
    public sealed class ScriptTaskOutcome
    {
        public bool Success { get; }
        public string TaskStatus { get; }
        public string ErrorCode { get; }
        public McpTaskResult TaskResult { get; }

        private ScriptTaskOutcome(bool success, string status, string code = null, McpTaskResult task = null)
        {
            Success = success;
            TaskStatus = status;
            ErrorCode = code;
            TaskResult = task;
        }

        public static ScriptTaskOutcome Evaluate(string contract, bool compileOnly, bool executionSucceeded,
            string execution, bool complete, string nativeOutcome, object returned)
        {
            ScriptModes.Check(contract, ScriptModes.Legacy, ScriptModes.TaskV1);
            if (compileOnly || contract == ScriptModes.Legacy)
                return new ScriptTaskOutcome(executionSucceeded, "not_reported");

            var task = returned as McpTaskResult;
            if (execution != "completed" || !complete)
                return new ScriptTaskOutcome(executionSucceeded && (task == null || task.IsPassed),
                    task == null ? "unknown" : task.IsPassed ? "passed" : "failed",
                    task == null || task.IsPassed ? null : task.ErrorCode, task);
            if (task == null)
                return new ScriptTaskOutcome(false, "not_reported",
                    executionSucceeded ? ScriptErrorCodes.TaskResultRequired : null);

            var nativeDenied = nativeOutcome == "rolled_back" || nativeOutcome == "partial" ||
                               nativeOutcome == "cancelled" || nativeOutcome == "not_started" || nativeOutcome == "failed";
            return new ScriptTaskOutcome(executionSucceeded && task.IsPassed && !nativeDenied,
                task.IsPassed ? "passed" : "failed", task.IsPassed ? null : task.ErrorCode, task);
        }
    }
}
