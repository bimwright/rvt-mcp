using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin.Views;
using Xunit;

namespace RvtMcp.Tests
{
    public class FailureIssuesPresentationTests
    {
        private static FailureIncident Incident()
        {
            var definition = Guid.NewGuid().ToString();
            return new FailureIncident {
                ModelScopeKey = "scope-a", RequestHash = "request-a", RevitYear = 2027, DocumentRevision = "revision-current",
                OperationContext = "edit", State = "open", MessageCount = 2,
                Messages = new List<FailureMessage> {
                    new FailureMessage { DefinitionId = definition, Severity = "Warning",
                        ExistingElementIds = new List<long> { 10 }, FailingElementIds = new List<long> { 10, 999 } },
                    new FailureMessage { DefinitionId = definition, Severity = "Warning",
                        ExistingElementIds = new List<long> { 10, 11 }, FailingElementIds = new List<long> { 10, 11 } }
                }
            };
        }

        private static FailureIssuesContext Context() => new FailureIssuesContext {
            IsAvailable = true, ModelScopeKey = "scope-a", RevitYear = 2027,
            DocumentRevision = "revision-current", RevitUser = "reviewer", HostInstanceId = "host-a"
        };

        private static FailureOperation RolledBack() => new FailureOperation { Execution = "completed", Outcome = "rolled_back" };

        [Fact]
        public void Acceptance_collects_every_definition_and_only_known_existing_elements()
        {
            var incident = Incident();
            incident.Messages.Add(new FailureMessage {
                DefinitionId = Guid.NewGuid().ToString(), Severity = "Warning", FailingElementIds = new List<long> { 888 }
            });
            incident.MessageCount++;
            var before = DateTimeOffset.UtcNow;
            var decision = FailureIssuesPresentation.CreateDecision("accept_once", incident, RolledBack(), Context(), "Reviewed constraints", false);
            Assert.Equal(2, decision.Grants.Count);
            Assert.Equal(2, decision.Grants[0].MaxCount);
            Assert.Equal(new long[] { 10, 11 }, decision.Grants[0].ExistingElementIds);
            Assert.Empty(decision.Grants[1].ExistingElementIds);
            Assert.All(decision.Grants, grant => {
                Assert.Equal(incident.RequestHash, grant.RequestHash);
                Assert.Equal("revision-current", grant.DocumentRevision);
                Assert.Equal("scope-a", grant.ModelScopeKey);
                Assert.Equal(2027, grant.RevitYear);
                Assert.Equal("edit", grant.OperationContext);
                Assert.InRange(grant.ExpiresUtc, before.AddMinutes(5), DateTimeOffset.UtcNow.AddMinutes(5));
            });
            Assert.Equal("revit_ui", decision.Channel);
        }

        [Theory]
        [InlineData("Error")]
        [InlineData("DocumentCorruption")]
        [InlineData("Unknown")]
        public void Mixed_or_unsupported_severity_cannot_be_accepted(string severity)
        {
            var incident = Incident(); incident.Messages[1].Severity = severity;
            Assert.False(FailureIssuesPresentation.CanAcceptWarning(incident));
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                "create_rule", incident, RolledBack(), Context(), "Review", false));
        }

        [Fact]
        public void Missing_counts_incomplete_messages_and_unknown_codes_do_not_create_rules()
        {
            var incident = Incident();
            incident.MessageCount++;
            Assert.False(FailureIssuesPresentation.CanAcceptWarning(incident));
            incident.MessageCount--;
            incident.Messages[0].Complete = false;
            Assert.False(FailureIssuesPresentation.CanAcceptWarning(incident));
            incident.Messages[0].Complete = true;
            incident.OmittedElementCount = 1;
            Assert.False(FailureIssuesPresentation.CanAcceptWarning(incident));
            incident.OmittedElementCount = 0;
            incident.Messages[0].DefinitionId = "unknown";
            Assert.False(FailureIssuesPresentation.CanAcceptWarning(incident));
        }

        [Theory]
        [InlineData("partial", "rework")]
        [InlineData("unknown", "abandon")]
        public void Closing_uncertain_outcome_requires_human_model_verification(string outcome, string action)
        {
            var operation = new FailureOperation { Execution = "completed", Outcome = outcome };
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                action, Incident(), operation, Context(), "Reviewed", false));
            var decision = FailureIssuesPresentation.CreateDecision(action, Incident(), operation, Context(), "Reviewed", true);
            Assert.Equal("human_asserted", decision.Verification);
            Assert.True(decision.VerifiedModel);
            Assert.Null(decision.Grant);
            Assert.Empty(decision.Grants);
            Assert.Empty(decision.Rules);
        }

        [Fact]
        public void Decisions_require_same_active_model_and_open_incident()
        {
            var incident = Incident(); var context = Context();
            context.ModelScopeKey = "different-model";
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                "rework", incident, RolledBack(), context, "Reviewed", false));
            context.ModelScopeKey = incident.ModelScopeKey;
            incident.State = "accepted_once";
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                "rework", incident, RolledBack(), context, "Reviewed", false));
        }

        [Theory]
        [InlineData("rework")]
        [InlineData("abandon")]
        [InlineData("accept_once")]
        [InlineData("create_rule")]
        public void Blank_reason_records_unspecified_without_claiming_model_verification(string action)
        {
            foreach (var reason in new[] { null, "", " \t\r\n" })
            {
                var decision = FailureIssuesPresentation.CreateDecision(
                    action, Incident(), RolledBack(), Context(), reason, false);
                Assert.Equal("No reason provided", decision.Reason);
                Assert.Equal(action, decision.Action);
                Assert.False(decision.VerifiedModel);
                Assert.Null(decision.Verification);
            }
        }

        [Fact]
        public void Blank_reason_does_not_bypass_model_review_or_acceptance_constraints()
        {
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                "abandon", Incident(), new FailureOperation { Execution = "completed", Outcome = "partial" },
                Context(), null, false));
            var incomplete = Incident();
            incomplete.Complete = false;
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                "accept_once", incomplete, RolledBack(), Context(), "", true));
            var context = Context();
            context.ModelScopeKey = "different-model";
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                "rework", Incident(), RolledBack(), context, " ", true));
        }

        [Fact]
        public void Rule_is_bound_to_exact_year_and_each_warning_group()
        {
            var incident = Incident(); var context = Context();
            context.RevitYear = 2024;
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                "create_rule", incident, RolledBack(), context, "Reviewed", false));
            context.RevitYear = 2027;
            var decision = FailureIssuesPresentation.CreateDecision("create_rule", incident, RolledBack(), context, "  Reviewed  ", false);
            Assert.Equal("Reviewed", decision.Reason);
            Assert.Single(decision.Rules);
            Assert.Equal(incident.Messages[0].DefinitionId, decision.Rules.Single().DefinitionId);
            Assert.Equal(2, decision.Rules.Single().MaxCount);
        }

        [Fact]
        public void Changed_revision_requires_explicit_review_before_new_one_use_permission()
        {
            var incident = Incident(); var context = Context(); context.DocumentRevision = "after-rollback";
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision(
                "accept_once", incident, RolledBack(), context, "Reviewed", false));
            var decision = FailureIssuesPresentation.CreateDecision("accept_once", incident, RolledBack(), context, "Reviewed current model", true);
            Assert.Equal("after-rollback", decision.DocumentRevision);
            Assert.All(decision.Grants, grant => Assert.Equal("after-rollback", grant.DocumentRevision));
            Assert.Equal(incident.RequestHash, decision.Grants[0].RequestHash);
        }

        [Fact]
        public void Only_exact_supported_dialog_context_can_produce_a_rule()
        {
            var incident = Incident();
            incident.Messages = new List<FailureMessage> { new FailureMessage {
                DefinitionId = "TaskDialog_Unresolved_References", Severity = "TaskDialog"
            } };
            incident.MessageCount = 1;
            incident.OperationContext = "open:ignore_unresolved_references";
            var context = Context();
            Assert.NotNull(FailureIssuesPresentation.DialogAdapter(incident));
            var decision = FailureIssuesPresentation.CreateDecision("create_rule", incident, RolledBack(), context, "Allow this exact opening prompt", false);
            Assert.Equal("TaskDialog", decision.Rules.Single().Severity);
            Assert.Equal(1, decision.Rules.Single().MaxCount);
            incident.OperationContext = "edit";
            Assert.Null(FailureIssuesPresentation.DialogAdapter(incident));
            incident.OperationContext = "open:ignore_unresolved_references";
            incident.RevitYear = 2024;
            Assert.Null(FailureIssuesPresentation.DialogAdapter(incident));
        }

        [Fact]
        public void Observed_supported_dialog_can_make_future_rule_but_not_replay_permission()
        {
            var incident = Incident(); incident.State = "observed";
            incident.Messages = new List<FailureMessage> { new FailureMessage {
                DefinitionId = "TaskDialog_Views_Related_To_Analytical_Changed", Severity = "TaskDialog"
            } };
            incident.MessageCount = 1; incident.OperationContext = "open:acknowledge_analytical_changes";
            Assert.Single(FailureIssuesPresentation.CreateDecision("create_rule", incident, RolledBack(), Context(), "Future exact context only", false).Rules);
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision("accept_once", incident, RolledBack(), Context(), "Do not replay", false));
            Assert.Throws<InvalidOperationException>(() => FailureIssuesPresentation.CreateDecision("abandon", incident, RolledBack(), Context(), "Already observed", false));
        }
    }
}
