using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Opt-in failure handling for typed/send-code transactions and StairsEditScope.Commit.
    /// Inside an MCP scope, captures failures and applies the shared model policy.
    /// Outside that scope, preserves the existing opt-in warning/error behavior.
    /// Callers must report HadWarnings/Messages and inspect HadErrors and commit status.
    /// </summary>
    public sealed class SafeFailuresPreprocessor : IFailuresPreprocessor
    {
        private readonly List<string> _messages = new List<string>();
        public IReadOnlyList<string> Messages => _messages;
        public bool HadErrors { get; private set; }
        public bool HadWarnings { get; private set; }
        public bool WasRolledBack { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            if (Failures.FailureControl.Active != null)
            {
                // Capture the compatibility fields without deleting messages before the policy sees them.
                try
                {
                    foreach (var failure in accessor.GetFailureMessages())
                    {
                        var severity = failure.GetSeverity();
                        if (_messages.Count < 5000) _messages.Add(severity + ": " + failure.GetDescriptionText());
                        HadWarnings |= severity == FailureSeverity.Warning;
                        HadErrors |= severity != FailureSeverity.None && severity != FailureSeverity.Warning;
                    }
                }
                catch { /* The shared adapter records a capture failure and rolls back. */ }
                var decision = Failures.FailureControl.Active.Process(accessor, "preprocessor");
                WasRolledBack |= decision == FailureProcessingResult.ProceedWithRollBack;
                return decision;
            }
            bool hasError = false;
            foreach (var failure in accessor.GetFailureMessages())
            {
                var severity = failure.GetSeverity();
                _messages.Add(severity + ": " + failure.GetDescriptionText());
                if (severity == FailureSeverity.Warning)
                {
                    HadWarnings = true;
                    accessor.DeleteWarning(failure);
                }
                else if (severity != FailureSeverity.None)
                    hasError = true;
            }

            if (!hasError) return FailureProcessingResult.Continue;
            HadErrors = true;
            WasRolledBack = true;
            accessor.SetFailureHandlingOptions(accessor.GetFailureHandlingOptions().SetClearAfterRollback(true));
            return FailureProcessingResult.ProceedWithRollBack;
        }
    }
}
