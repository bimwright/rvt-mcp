using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    public class FailureCoreTests
    {
        internal static FailureMessage Warning(string code = "warning_a", params long[] ids)
        { return new FailureMessage { DefinitionId = code, Severity = "Warning", Description = "Native warning", FailingElementIds = ids.ToList(), ExistingElementIds = ids.ToList() }; }
        internal static FailureRule Rule(string code = "warning_a", params long[] ids)
        { return new FailureRule { ModelScopeKey = "model_a", DefinitionId = code, RevitYear = 2024, OperationContext = "edit", MaxCount = 1, ExistingElementIds = ids.ToList() }; }
        private static FailurePolicyResult Policy(IEnumerable<FailureMessage> messages, bool complete = true, IEnumerable<FailureRule> rules = null, IEnumerable<FailureGrant> grants = null)
        { return FailurePolicy.Evaluate(messages, complete, "model_a", 2024, "edit", "request_a", "revision_a", rules, grants); }

        [Fact] public void EmptyPolicyStopsWarningsAndAllSeriousOrUnknownSeverities()
        {
            Assert.Equal("continue", Policy(new FailureMessage[0]).Action);
            foreach (var severity in new[] { "Warning", "Error", "DocumentCorruption", "Unknown", "unexpected" })
            { var failure = Warning(); failure.Severity = severity; Assert.Equal("rollback", Policy(new[] { failure }).Action); }
        }
        [Fact] public void EveryDefinitionCountExistingElementAndContextMustMatch()
        {
            var rule = Rule("warning_a", 10); var a = Warning("warning_a", 10);
            Assert.Equal("continue", Policy(new[] { a }, rules: new[] { rule }).Action);
            Assert.Equal("rollback", Policy(new[] { a, Warning("warning_b", 10) }, rules: new[] { rule }).Action);
            Assert.Equal("rollback", Policy(new[] { a, a }, rules: new[] { rule }).Action);
            Assert.Equal("rollback", Policy(new[] { Warning("warning_a", 11) }, rules: new[] { rule }).Action);
            rule.RevitYear = 2027; Assert.Equal("rollback", Policy(new[] { a }, rules: new[] { rule }).Action);
            rule.RevitYear = 2024; rule.ModelScopeKey = "other"; Assert.Equal("rollback", Policy(new[] { a }, rules: new[] { rule }).Action);
        }
        [Fact] public void IncompleteCaptureAndMixedErrorCannotBeAllowed()
        {
            var warning = Warning(); var rule = Rule();
            Assert.Equal("rollback", Policy(new[] { warning }, false, new[] { rule }).Action);
            warning.Complete = false; Assert.Equal("rollback", Policy(new[] { warning }, rules: new[] { rule }).Action);
            warning.Complete = true; var error = Warning(); error.Severity = "Error";
            Assert.Equal("rollback", Policy(new[] { warning, error }, rules: new[] { rule }).Action);
        }
        [Fact] public void GrantIsBoundToFingerprintRevisionExpiryAndConsumption()
        {
            var grant = new FailureGrant { ModelScopeKey = "model_a", DefinitionId = "warning_a", RevitYear = 2024,
                MaxCount = 1, OperationContext = "edit", RequestHash = "request_a", DocumentRevision = "revision_a" };
            Assert.Equal("continue", Policy(new[] { Warning() }, grants: new[] { grant }).Action);
            grant.RequestHash = "other"; Assert.Equal("rollback", Policy(new[] { Warning() }, grants: new[] { grant }).Action);
            grant.RequestHash = "request_a"; grant.DocumentRevision = "other"; Assert.Equal("rollback", Policy(new[] { Warning() }, grants: new[] { grant }).Action);
            grant.DocumentRevision = "revision_a"; grant.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1); Assert.Equal("rollback", Policy(new[] { Warning() }, grants: new[] { grant }).Action);
            grant.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2); grant.ConsumedOperationId = "previous"; Assert.Equal("rollback", Policy(new[] { Warning() }, grants: new[] { grant }).Action);
        }
        [Fact] public void RetryCreationIdsAreNotStableButNewExistingIdsRemainConstrained()
        {
            var message = Warning(); message.FailingElementIds.Add(9999);
            Assert.Equal("continue", Policy(new[] { message }, rules: new[] { Rule() }).Action);
            message.ExistingElementIds.Add(42);
            Assert.Equal("rollback", Policy(new[] { message }, rules: new[] { Rule() }).Action);
        }
        [Fact] public void DedupRequiresExactCrossAdapterTransactionCycleSequence()
        {
            var a = Warning(); a.TransactionKey = "tx"; a.Cycle = "cycle"; a.Sequence = 1; a.Source = "helper";
            var b = Warning(); b.TransactionKey = "tx"; b.Cycle = "cycle"; b.Sequence = 1; b.Source = "global";
            var bounded = FailureCapture.Bound(new FailureIncident { Messages = new List<FailureMessage> { a, b } });
            Assert.Single(bounded.Messages);
            b.Cycle = "next"; Assert.Equal(2, FailureCapture.Bound(new FailureIncident { Messages = new List<FailureMessage> { a, b } }).Messages.Count);
            a.Cycle = b.Cycle = null; Assert.Equal(2, FailureCapture.Bound(new FailureIncident { Messages = new List<FailureMessage> { a, b } }).Messages.Count);
            a.Cycle = b.Cycle = "cycle"; b.Source = "helper"; Assert.Equal(2, FailureCapture.Bound(new FailureIncident { Messages = new List<FailureMessage> { a, b } }).Messages.Count);
        }
        [Fact] public void CaptureBoundsRetainIDsAndMarkOmissions()
        {
            var messages = Enumerable.Range(0, 5001).Select(n => Warning("w_" + n, n)).ToList();
            var bound = FailureCapture.Bound(new FailureIncident { Messages = messages });
            Assert.Equal(5000, bound.Messages.Count); Assert.False(bound.Complete); Assert.Equal(1, bound.OmittedMessageCount);
            Assert.Equal(5001, bound.MessageCount); Assert.Equal("at_least", bound.CountKind);
            var overflow = Warning(); overflow.FailingElementIds = Enumerable.Range(1, 50001).Select(i => (long)i).ToList();
            bound = FailureCapture.Bound(new FailureIncident { Messages = new List<FailureMessage> { overflow } });
            Assert.Equal(50000, bound.Messages[0].FailingElementIds.Count); Assert.False(bound.Complete);
        }
        [Fact] public void SummarySupportsLargeUnicodeAndNeverExposesLocalPathsOrTokens()
        {
            var messages = Enumerable.Range(0, 294).Select(n => Warning("warning_" + n, n)).ToList();
            foreach (var m in messages) m.Description = @"C:\Users\Admin\private.rvt bearer abcdefghijkl " + new string('界', 3000);
            var summary = FailureCapture.Summary(new FailureIncident { OperationId = "op", ModelScopeKey = "model_a", Messages = messages }, false, "blocked");
            var json = summary.ToString(Newtonsoft.Json.Formatting.None);
            Assert.True(Encoding.UTF8.GetByteCount(json) <= 16384); Assert.DoesNotContain("private.rvt", json); Assert.DoesNotContain("abcdefghijkl", json);
            Assert.Equal(294, summary.Value<int>("messageCount")); Assert.NotNull(summary["incidentIds"]); Assert.Equal("rolled_back", summary.Value<string>("action"));
        }
        [Fact] public void NextStepsPreserveTheCurrentFailureButDoNotLockLaterCommands()
        {
            var rolledBack = FailureCapture.NextStep("REVIT_FAILURE_ROLLED_BACK", false, false);
            Assert.Contains("rolled it back", rolledBack); Assert.Contains("survey", rolledBack);
            Assert.Contains("revise the plan", rolledBack); Assert.Contains("stays writable", rolledBack);
            Assert.Contains("History > Issues", rolledBack);
            foreach (var code in new[] { "REVIT_REQUEST_REPEATED", "REVIT_MODEL_BLOCKED", "FAILURE_STORAGE_FAILED", "FAILURE_CAPTURE_INCOMPLETE", null })
            {
                var next = FailureCapture.NextStep(code, true, false);
                Assert.Contains("Previous Issues do not block subsequent MCP commands or repeated requests", next);
                Assert.DoesNotContain("writes to this model are blocked", next);
                Assert.DoesNotContain("same request is refused", next);
            }
            var partial = FailureCapture.NextStep("REVIT_FAILURE_ROLLED_BACK", true, false);
            Assert.Contains("earlier changes may remain", partial);
            Assert.DoesNotContain("left the model unchanged", partial);
            Assert.Contains("needs the user", FailureCapture.NextStep("REVIT_OPERATION_REQUIRES_USER", true, false));
            Assert.Equal(FailureCapture.NotStartedNextStep, FailureCapture.NextStep("REVIT_BUSY", false, false));
            var accepted = FailureCapture.NextStep(null, false, true);
            Assert.Contains("user accepted", accepted); Assert.Contains("remain in the model", accepted);
            var summary = FailureCapture.Summary(new FailureIncident { OperationId = "op", ModelScopeKey = "model_a", Messages = { Warning() } }, false, "open");
            Assert.Equal(rolledBack, summary.Value<string>("nextStep"));
            var advisory = FailureCapture.Summary(new FailureIncident { OperationId = "op", ModelScopeKey = "model_a", Complete = false }, null, "blocked");
            Assert.Equal("open", advisory.Value<string>("gate")); Assert.True(advisory.Value<bool>("requiresReview"));
        }
        [Fact] public void ExactDialogAdaptersRejectUnverifiedYearsContextsAndGenericDialogs()
        {
            const string id = "TaskDialog_Unresolved_References", context = "open:ignore_unresolved_references";
            Assert.Equal(1002, FailureDialogAdapters.Get(id, 2022, context).Result);
            Assert.Equal(1002, FailureDialogAdapters.Get(id, 2027, context).Result);
            Assert.Null(FailureDialogAdapters.Get(id, 2024, context)); Assert.Null(FailureDialogAdapters.Get(id, 2027, "edit"));
            Assert.Null(FailureDialogAdapters.Get("Dialog_Revit_DocWarnDialog", 2027, context));
            var rule = new FailureRule { ModelScopeKey = "model_a", Severity = "TaskDialog", DefinitionId = id, RevitYear = 2027, OperationContext = context, MaxCount = 1 };
            Assert.Equal("continue", FailurePolicy.EvaluateDialog(id, 2027, context, "model_a", "request", "rev", new[] { rule }).Action);
            rule.OperationContext = "edit";
            Assert.Equal("rollback", FailurePolicy.EvaluateDialog(id, 2027, context, "model_a", "request", "rev", new[] { rule }).Action);
        }
        [Fact] public void TextTruncationAndOversizedIdentityCannotBeAcceptedOrDeduplicated()
        {
            var a = Warning(); a.Description = new string('x', 5000); a.TransactionKey = "tx"; a.Cycle = "1"; a.Sequence = 1; a.Source = "helper";
            var b = Warning(); b.Description = new string('x', 5000); b.TransactionKey = "tx"; b.Cycle = "1"; b.Sequence = 1; b.Source = "global";
            var bounded = FailureCapture.Bound(new FailureIncident { Messages = new List<FailureMessage> { a, b } });
            Assert.Equal(2, bounded.Messages.Count); Assert.False(bounded.Complete); Assert.All(bounded.Messages, m => Assert.True(m.TextTruncated));
            Assert.Equal(5000, a.Description.Length); // capture boundary must not mutate the caller's facts
            Assert.Equal("rollback", Policy(bounded.Messages, bounded.Complete, new[] { Rule() }).Action);
        }
    }
}
