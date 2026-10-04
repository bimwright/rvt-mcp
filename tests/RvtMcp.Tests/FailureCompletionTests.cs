using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    public class FailureCompletionTests
    {
        private static FailureOperation Complete(FailureCompletionFacts facts, bool dynamicCode = false, bool captureComplete = true)
        {
            var operation = new FailureOperation {
                ObservationCoverage = dynamicCode ? "dynamic_code_unverified" : "supported_handlers_only",
                Complete = captureComplete
            };
            FailureCompletion.Apply(operation, facts);
            return operation;
        }

        private static void AssertOutcome(FailureOperation operation, string outcome, bool? mutationApplied, bool requiresReview)
        {
            Assert.Equal(outcome, operation.Outcome);
            Assert.Equal(mutationApplied, operation.MutationApplied);
            Assert.Equal(requiresReview, operation.RequiresReview);
        }

        [Fact] public void RejectedBeforeExecutionDoesNotClaimRollbackOrMutation()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { PolicyStopped = true }), "not_started", false, false);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OrdinaryReadDoesNotRequireReviewWhetherHandlerSucceedsOrFails(bool succeeds)
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, IsReadOnly = true,
                HandlerSucceeded = succeeds }), "not_started", false, false);
        }

        [Fact] public void SuccessfulDynamicCodeWithoutObservedEffectsDoesNotProveNoMutationOrCreateBlanketGate()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, HandlerSucceeded = true },
                dynamicCode: true), "unknown", null, false);
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(false, false, false)]
        public void ActualUncertaintyInDynamicCodeRequiresReview(bool nativeUnknown, bool storageFailed, bool captureComplete)
        {
            var operation = Complete(new FailureCompletionFacts { ExecutionStarted = true, HandlerSucceeded = true,
                NativeOutcomeUnknown = nativeUnknown, StorageFailed = storageFailed }, dynamicCode: true, captureComplete: captureComplete);
            Assert.True(operation.RequiresReview);
            Assert.Null(operation.MutationApplied);
        }

        [Fact] public void FailedDynamicCodeCannotUseUnverifiedSuccessException()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true }, dynamicCode: true), "unknown", null, true);
        }

        [Fact] public void NativeSaveBeforeBlockedSecondModelRemainsPartial()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, NativeCommitted = true,
                PolicyStopped = true }), "partial", true, false);
        }

        [Fact] public void NativeSaveBeforeUnknownNativeOutcomeRetainsConfirmedEffects()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, NativeCommitted = true,
                NativeOutcomeUnknown = true }, captureComplete: false), "partial", true, true);
        }

        [Fact] public void GroupRollbackCannotUndoEarlierNativeSave()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, NativeCommitted = true,
                GroupRolledBack = true, PolicyStopped = true }), "partial", true, false);
        }

        [Fact] public void ConfirmedGroupRollbackProvesNoRetainedModelChanges()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, GroupRolledBack = true,
                PolicyStopped = true }), "rolled_back", false, false);
        }

        [Fact] public void FailureAfterEarlierCommitRemainsPartial()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, AnyCommittedChanges = true }), "partial", true, false);
        }

        [Fact] public void ConfirmedNativeCancellationWithoutPriorEffectsIsNotMutation()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, NativeCancelled = true,
                PolicyStopped = true }), "cancelled", false, false);
        }

        [Fact] public void FailedMutationWithoutOutcomeEvidenceRequiresReview()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true }), "unknown", null, true);
        }

        [Fact] public void ObservedUncertaintyInReadMustNotBeTreatedAsOrdinaryRead()
        {
            AssertOutcome(Complete(new FailureCompletionFacts { ExecutionStarted = true, IsReadOnly = true,
                HasIncidents = true, HasPersistedRecord = true, NativeOutcomeUnknown = true }, captureComplete: false), "unknown", null, true);
        }

        [Theory]
        [InlineData("running")]
        [InlineData("waiting_for_user")]
        [InlineData("timed_out_running")]
        public void CallerWaitStateCannotChangeConfirmedCompletionFacts(string execution)
        {
            var operation = new FailureOperation { Execution = execution };
            FailureCompletion.Apply(operation, new FailureCompletionFacts { ExecutionStarted = true,
                HandlerSucceeded = true, AnyCommittedChanges = true });
            AssertOutcome(operation, "committed", true, false);
            Assert.Equal(execution, operation.Execution);
        }
    }
}
