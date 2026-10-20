using System;
using System.Collections.Generic;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests;

public class WarningPolicyTests
{
    private static FailureMessage Warning() => new() { DefinitionId = "cf531a66-c854-4e14-bb9b-fd5b6a48e5c9", Severity = "Warning", Description = "Fixture warning" };

    [Fact]
    public void Default_is_rollback_but_explicit_accept_and_log_allows_complete_warnings_without_rules()
    {
        var warning = Warning();
        Assert.Equal("rollback", FailurePolicy.Evaluate(new[] { warning }, true, "model", 2027, "edit", "request", "revision").Action);
        var accepted = FailurePolicy.Evaluate(new[] { warning }, true, "model", 2027, "edit", "request", "revision", warningPolicy: "accept-and-log");
        Assert.Equal("continue", accepted.Action);
        Assert.Equal("accepted_by_policy", accepted.Reason);
        Assert.Empty(accepted.RuleIds);
        Assert.Empty(accepted.GrantIds);
    }

    [Theory]
    [InlineData("accepted_by_policy", "accept-and-log", "accept-and-log")]
    [InlineData("accepted_by_rule", "rollback", "rule or one-use")]
    [InlineData("accepted_once", "rollback", "rule or one-use")]
    public void Accepted_warning_summaries_describe_acceptance_instead_of_rollback(string state, string mode, string expected)
    {
        var incident = new FailureIncident { State = state, Action = "accepted_warning", WarningPolicy = mode, Messages = { Warning() } };
        var summary = FailureCapture.Summary(incident, true, "open");
        Assert.Contains(expected, summary["nextStep"]!.ToString());
        Assert.DoesNotContain("rolled it back", summary["nextStep"]!.ToString());
    }

    [Fact]
    public void A_later_rule_decision_does_not_relabel_the_original_rollback_as_committed()
    {
        var incident = new FailureIncident { State = "accepted_by_rule", Action = "rolled_back", Messages = { Warning() } };
        var summary = FailureCapture.Summary(incident, false, "open");
        Assert.Contains("rolled it back", summary["nextStep"]!.ToString());
        Assert.DoesNotContain("Committed with warnings", summary["nextStep"]!.ToString());
    }

    [Theory]
    [InlineData("Error", true)]
    [InlineData("DocumentCorruption", true)]
    [InlineData("Unknown", true)]
    [InlineData("TaskDialog", true)]
    [InlineData("Warning", false)]
    public void Accept_and_log_does_not_accept_errors_dialogs_or_incomplete_capture(string severity, bool complete)
    {
        var message = Warning(); message.Severity = severity;
        Assert.Equal("rollback", FailurePolicy.Evaluate(new[] { message }, complete, "model", 2027, "edit", "request", "revision", warningPolicy: "accept-and-log").Action);
    }

    [Fact]
    public void A_mixed_error_or_truncated_message_still_rolls_back()
    {
        var truncated = Warning(); truncated.TextTruncated = true;
        var error = Warning(); error.Severity = "Error";
        foreach (var messages in new[] { new[] { Warning(), error }, new[] { truncated }, new[] { new FailureMessage { Severity = "Warning" } } })
            Assert.Equal("rollback", FailurePolicy.Evaluate(messages, true, "model", 2027, "edit", "request", "revision", warningPolicy: "accept-and-log").Action);
    }
}
