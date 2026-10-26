using RvtMcp.Scripting;

namespace RvtMcp.Plugin
{
    /// <summary>Explicit script task result; transport/native completion remains separate.</summary>
    public sealed class McpTaskResult
    {
        public bool IsPassed { get; }
        public object Value { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }
        public object Details { get; }

        private McpTaskResult(bool passed, object value, string code, string message, object details)
        {
            IsPassed = passed;
            Value = value;
            ErrorCode = code;
            ErrorMessage = message;
            Details = details;
        }

        public static McpTaskResult Pass(object value = null)
            => new McpTaskResult(true, value, null, null, null);

        public static McpTaskResult Fail(string code, string message, object details = null)
            => new McpTaskResult(false, null, string.IsNullOrWhiteSpace(code) ? ScriptErrorCodes.TaskFailed : code, message, details);
    }
}
