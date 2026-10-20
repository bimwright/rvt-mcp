using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests;

public class WarningPolicyLedgerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvt-warning-ledger-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Policy_acceptance_requires_an_opted_in_operation_and_complete_durable_evidence(bool optedIn, bool oversized)
    {
        var store = new FailureLedgerStore(Path.Combine(_folder, "ledger.db"));
        var model = store.ResolveModel(new FailureModelIdentity { PhysicalKey = "fixture", ModelKey = "fixture", Title = "Fixture" });
        var operation = new FailureOperation { WarningPolicy = optedIn ? "accept-and-log" : "rollback", ModelScopeKeys = new List<string> { model.ModelScopeKey } };
        Assert.True(store.SaveOperation(operation).Recorded);
        var incident = new FailureIncident {
            OperationId = operation.OperationId, ModelScopeKey = model.ModelScopeKey, RevitYear = 2027,
            WarningPolicy = "accept-and-log", State = "accepted_by_policy", Action = "accepted_warning", MessageCount = 1,
            Messages = new List<FailureMessage> { new() { DefinitionId = Guid.NewGuid().ToString(), Severity = "Warning", Description = oversized ? new string('x', 5000) : "Fixture warning" } }
        };
        var result = store.RecordIncident(incident);
        if (optedIn && !oversized)
        {
            Assert.True(result.Recorded);
            Assert.Equal("accepted_by_policy", Assert.Single(store.Query(new FailureRecordQuery { ModelScopeKey = model.ModelScopeKey, State = "accepted_by_policy", IncludeDetails = true }).Records).State);
        }
        else
        {
            Assert.False(result.Recorded);
            Assert.Empty(store.Query(new FailureRecordQuery { ModelScopeKey = model.ModelScopeKey }).Records);
        }
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
