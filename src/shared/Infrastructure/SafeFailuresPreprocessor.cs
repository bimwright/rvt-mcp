using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Opt-in failure handling for send-code transactions and StairsEditScope.Commit.
    /// Records and deletes warnings; unresolved errors force a silent rollback.
    /// Callers must report HadWarnings/Messages and inspect HadErrors and commit status.
    /// </summary>
    public sealed class SafeFailuresPreprocessor : IFailuresPreprocessor
    {
        private readonly List<string> _messages = new List<string>();
        public IReadOnlyList<string> Messages => _messages;
        public bool HadErrors { get; private set; }
        public bool HadWarnings { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
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
            accessor.SetFailureHandlingOptions(accessor.GetFailureHandlingOptions().SetClearAfterRollback(true));
            return FailureProcessingResult.ProceedWithRollBack;
        }
    }
}
