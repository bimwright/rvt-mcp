using System;

namespace RvtMcp.Failures
{
    public sealed class FailureCompletionFacts
    {
        public bool ExecutionStarted { get; set; }
        public bool HandlerSucceeded { get; set; }
        public bool IsReadOnly { get; set; }
        public bool HasPersistedRecord { get; set; }
        public bool HasIncidents { get; set; }
        public bool PolicyStopped { get; set; }
        public bool NativeCancelled { get; set; }
        public bool GroupRolledBack { get; set; }
        public bool AnyCommittedChanges { get; set; }
        public bool NativeCommitted { get; set; }
        public bool StorageFailed { get; set; }
        public bool NativeOutcomeUnknown { get; set; }
    }

    // Execution completion is judged from native facts, never from a caller timeout.
    public static class FailureCompletion
    {
        public static void Apply(FailureOperation operation, FailureCompletionFacts facts)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (facts == null) throw new ArgumentNullException(nameof(facts));

            bool applied = facts.AnyCommittedChanges || facts.NativeCommitted;
            operation.Outcome = !facts.ExecutionStarted ? "not_started"
                : facts.GroupRolledBack ? (facts.NativeCommitted ? "partial" : "rolled_back")
                : facts.PolicyStopped ? (applied ? "partial" : facts.NativeCancelled ? "cancelled" : "rolled_back")
                : facts.HandlerSucceeded ? (facts.IsReadOnly ? "not_started" : "committed")
                : applied ? "partial" : "unknown";
            if (facts.NativeOutcomeUnknown) operation.Outcome = applied ? "partial" : "unknown";

            bool unverifiedNoChange = facts.ExecutionStarted && facts.HandlerSucceeded
                && !facts.PolicyStopped && !applied && !facts.NativeOutcomeUnknown && !facts.StorageFailed
                && operation.Complete && operation.ObservationCoverage == "dynamic_code_unverified";
            if (unverifiedNoChange) operation.Outcome = "unknown";
            operation.RequiresReview = !unverifiedNoChange
                && (operation.Outcome == "unknown" || facts.StorageFailed || !operation.Complete);

            bool ordinaryRead = facts.IsReadOnly && !facts.HasPersistedRecord && !facts.HasIncidents
                && !facts.PolicyStopped && !facts.NativeOutcomeUnknown && !facts.StorageFailed && !applied;
            if (ordinaryRead)
            {
                operation.Outcome = "not_started";
                operation.RequiresReview = false;
            }
            operation.MutationApplied = facts.NativeCommitted ? true : facts.GroupRolledBack ? false
                : facts.AnyCommittedChanges ? true
                : operation.Outcome == "not_started" || operation.Outcome == "rolled_back" || operation.Outcome == "cancelled"
                    ? (bool?)false : null;
        }
    }
}
