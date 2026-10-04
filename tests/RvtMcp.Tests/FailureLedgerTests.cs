using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    public class FailureLedgerTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-failure-tests-" + Guid.NewGuid().ToString("N"));
        private string Db { get { return Path.Combine(_root, "failures", "ledger.db"); } }
        private FailureLedgerStore Store() { return new FailureLedgerStore(Db); }
        private FailureModel Model(FailureLedgerStore store, string physical = "physical_a", string key = "path_a")
        { return store.ResolveModel(new FailureModelIdentity { PhysicalKey = physical, ModelKey = key, Title = "Sample", HostInstanceId = "host", LiveDocumentId = "doc" }); }
        private FailureOperation Operation(FailureLedgerStore store, string scope, string outcome = "rolled_back")
        {
            var op = new FailureOperation { ModelScopeKeys = new List<string> { scope }, HostInstanceId = "host", Execution = "completed", Outcome = outcome,
                RevitYear = 2024, RequestHash = "request_a", DocumentRevision = "revision_a", OperationContext = "edit" };
            Assert.True(store.SaveOperation(op).Recorded); return op;
        }
        private FailureIncident Incident(FailureLedgerStore store, string scope, FailureOperation op, string code = "warning_a")
        {
            var incident = new FailureIncident { ModelScopeKey = scope, OperationId = op.OperationId, RevitYear = op.RevitYear,
                RequestHash = op.RequestHash, DocumentRevision = op.DocumentRevision, OperationContext = op.OperationContext,
                Messages = new List<FailureMessage> { FailureCoreTests.Warning(code, 10) } };
            Assert.True(store.RecordIncident(incident).Recorded); return incident;
        }
        private FailureDecision Decision(FailureIncident i, string action = "rework")
        { return new FailureDecision { ModelScopeKey = i.ModelScopeKey, IncidentId = i.IncidentId, Action = action, Reason = "Reviewed in Revit", WindowsUser = "tester", VerifiedModel = true }; }
        private FailureRule Rule(FailureIncident i)
        { return new FailureRule { ModelScopeKey = i.ModelScopeKey, DefinitionId = i.Messages[0].DefinitionId, RevitYear = i.RevitYear, OperationContext = i.OperationContext, MaxCount = 1, ExistingElementIds = new List<long> { 10 } }; }

        [Fact] public void OrdinaryQueriesDoNotCreateDatabaseOrDirectories()
        {
            var store = new FailureLedgerStore(Db, readOnly: true);
            Assert.False(store.Query(new FailureRecordQuery()).Available); Assert.Null(store.GetOperation("missing"));
            Assert.Empty(store.GetModels()); Assert.Empty(store.GetRules("model")); Assert.False(store.GetGate("model").Blocked);
            Assert.False(Directory.Exists(_root)); Assert.Throws<InvalidOperationException>(() => store.Initialize());
        }
        [Fact] public void SamePhysicalFileAliasRetainsGateWhileCopyGetsSeparateScope()
        {
            var store = Store(); var first = Model(store); var op = Operation(store, first.ModelScopeKey); Incident(store, first.ModelScopeKey, op);
            var renamed = Model(store, key: "path_renamed"); Assert.Equal(first.ModelScopeKey, renamed.ModelScopeKey);
            var copy = Model(store, "physical_copy", "path_a"); Assert.NotEqual(first.ModelScopeKey, copy.ModelScopeKey);
            Assert.True(new FailureLedgerStore(Db, true).GetGate(renamed.ModelScopeKey).Blocked); Assert.False(store.GetGate(copy.ModelScopeKey).Blocked);
            var lost = store.ResolveModel(new FailureModelIdentity { KnownScopeKey = first.ModelScopeKey, HostInstanceId = "host", LiveDocumentId = "doc" });
            Assert.Equal(first.ModelScopeKey, lost.ModelScopeKey);
        }
        [Fact] public void TransientScopesDoNotFollowTitleOrCentralPhysicalEvidence()
        {
            var store = Store(); var normal = Model(store);
            var a = store.ResolveModel(new FailureModelIdentity { IsTransient = true, PhysicalKey = "physical_a", HostInstanceId = "host_a", LiveDocumentId = "a", Title = "Sample" });
            var b = store.ResolveModel(new FailureModelIdentity { IsTransient = true, PhysicalKey = "physical_a", HostInstanceId = "host_b", LiveDocumentId = "a", Title = "Sample" });
            Assert.NotEqual(normal.ModelScopeKey, a.ModelScopeKey); Assert.NotEqual(a.ModelScopeKey, b.ModelScopeKey); Assert.True(a.IsTransient);
        }
        [Fact] public void GateSurvivesNewStoreAndOnlyReviewedIncidentCloses()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey);
            var a = Incident(store, m.ModelScopeKey, op); var b = Incident(store, m.ModelScopeKey, op);
            Assert.True(new FailureLedgerStore(Db, true).GetGate(m.ModelScopeKey).Blocked);
            Assert.True(store.ApplyDecision(Decision(a)).Recorded); Assert.True(store.GetGate(m.ModelScopeKey).Blocked);
            Assert.True(store.ApplyDecision(Decision(b, "abandon")).Recorded); Assert.False(store.GetGate(m.ModelScopeKey).Blocked);
        }
        [Fact] public void OneUseGrantMustBeDurablyConsumedAndCannotAcceptNewExistingElements()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey); var incident = Incident(store, m.ModelScopeKey, op);
            var grant = new FailureGrant { ModelScopeKey = m.ModelScopeKey, DefinitionId = "warning_a", RevitYear = 2024, MaxCount = 1,
                ExistingElementIds = new List<long> { 10 }, RequestHash = op.RequestHash, DocumentRevision = op.DocumentRevision };
            var decision = Decision(incident, "accept_once"); decision.Grants.Add(grant); Assert.True(store.ApplyDecision(decision).Recorded);
            var retry = Operation(store, m.ModelScopeKey); var accepted = new FailureIncident { OperationId = retry.OperationId, ModelScopeKey = m.ModelScopeKey,
                State = "accepted_once", Action = "accepted_warning", RevitYear = 2024, OperationContext = "edit", RequestHash = op.RequestHash,
                DocumentRevision = op.DocumentRevision, Messages = new List<FailureMessage> { FailureCoreTests.Warning("warning_a", 10) }, GrantIds = new List<string> { grant.GrantId } };
            Assert.True(store.RecordIncident(accepted).Recorded); Assert.Equal(retry.OperationId, store.GetGrants(m.ModelScopeKey).Single().ConsumedOperationId);
            Assert.True(store.RecordIncident(accepted).Recorded); // duplicate callback is idempotent
            accepted.IncidentId = Guid.NewGuid().ToString("N"); Assert.False(store.RecordIncident(accepted).Recorded);
            Assert.True(store.GetGate(m.ModelScopeKey).Blocked);
        }
        [Fact] public void GroupedAcceptanceIsAtomicAndRulesNeverCrossModels()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey);
            var incident = new FailureIncident { ModelScopeKey = m.ModelScopeKey, OperationId = op.OperationId, RevitYear = 2024, OperationContext = "edit",
                Messages = new List<FailureMessage> { FailureCoreTests.Warning("warning_a", 10), FailureCoreTests.Warning("warning_b", 10) } };
            Assert.True(store.RecordIncident(incident).Recorded); var decision = Decision(incident, "create_rule"); decision.Rules.Add(Rule(incident));
            Assert.Throws<ArgumentException>(() => store.ApplyDecision(decision)); Assert.Empty(store.GetRules(m.ModelScopeKey)); Assert.True(store.GetGate(m.ModelScopeKey).Blocked);
            var second = Rule(incident); second.DefinitionId = "warning_b"; decision.Rules.Add(second);
            Assert.True(store.ApplyDecision(decision).Recorded); Assert.Equal(2, store.GetRules(m.ModelScopeKey).Count); Assert.False(store.GetGate(m.ModelScopeKey).Blocked);
            var other = Model(store, "physical_b"); Assert.Empty(store.GetRules(other.ModelScopeKey));
            var revoke = new FailureDecision { ModelScopeKey = m.ModelScopeKey, Action = "revoke_rule", Rule = decision.Rules[0], Reason = "No longer allowed", WindowsUser = "tester" };
            Assert.True(store.ApplyDecision(revoke).Recorded); Assert.False(store.GetRules(m.ModelScopeKey).Single(r => r.RuleId == revoke.Rule.RuleId).Enabled);
        }
        [Fact] public void PendingAnotherHostBlocksAndCrashReviewIsPerModel()
        {
            var store = Store(); var a = Model(store); var b = Model(store, "physical_b");
            var op = new FailureOperation { HostInstanceId = "dead_host", ModelScopeKeys = new List<string> { a.ModelScopeKey, b.ModelScopeKey } };
            Assert.True(store.SaveOperation(op).Recorded); Assert.True(store.GetGate(a.ModelScopeKey).Blocked);
            Assert.False(store.GetGate(a.ModelScopeKey, op.OperationId).Blocked); Assert.Equal(1, store.MarkInterruptedHosts(new HashSet<string> { "live_host" }));
            Assert.Equal("interrupted_unknown", store.GetOperation(op.OperationId).Execution);
            var incident = store.Query(new FailureRecordQuery { ModelScopeKey = a.ModelScopeKey, IncludeDetails = true }).Records.Single();
            var decision = Decision(incident); decision.VerifiedModel = false; Assert.Throws<ArgumentException>(() => store.ApplyDecision(decision));
            decision.VerifiedModel = true; Assert.True(store.ApplyDecision(decision).Recorded); Assert.False(store.GetGate(a.ModelScopeKey).Blocked);
            Assert.True(store.GetGate(b.ModelScopeKey).Blocked); Assert.Equal("unknown", store.GetOperation(op.OperationId).Outcome);
        }
        [Fact] public void PaginationKeepsHighWatermarkAndRejectsFilterChange()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey);
            for (var i = 0; i < 4; i++) Incident(store, m.ModelScopeKey, op);
            var query = new FailureRecordQuery { ModelScopeKey = m.ModelScopeKey, Limit = 2, IncludeDetails = true };
            var first = store.Query(query); Assert.Equal(2, first.Records.Count); Assert.NotNull(first.NextCursor);
            Incident(store, m.ModelScopeKey, op); query.Cursor = first.NextCursor;
            var second = store.Query(query); Assert.Equal(2, second.Records.Count); Assert.Null(second.NextCursor);
            Assert.Empty(first.Records.Select(r => r.IncidentId).Intersect(second.Records.Select(r => r.IncidentId)));
            query.State = "open"; Assert.Throws<ArgumentException>(() => store.Query(query));
        }
        [Fact] public void SqliteLockProducesDurableFallbackAndRecoveryNeverReplaysOrAccepts()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey);
            var blocked = new FailureIncident { OperationId = op.OperationId, ModelScopeKey = m.ModelScopeKey, Messages = new List<FailureMessage> { FailureCoreTests.Warning() } };
            using (var connection = new SqliteConnection("Data Source=" + Db + ";Pooling=False"))
            {
                connection.Open(); using (var tx = connection.BeginTransaction())
                {
                    var watch = Stopwatch.StartNew(); var result = store.RecordIncident(blocked); watch.Stop();
                    Assert.Equal("fallback", result.Persistence); Assert.True(result.Durable); Assert.InRange(watch.Elapsed.TotalSeconds, 0, 3);
                    Assert.True(new FailureLedgerStore(Db, true).GetGate(m.ModelScopeKey).Blocked);
                }
            }
            Assert.Equal(1, store.RecoverPending()); Assert.Equal(0, store.RecoverPending());
            var recovered = store.Query(new FailureRecordQuery { IncidentId = blocked.IncidentId }).Records.Single();
            Assert.Equal("open", recovered.State); Assert.Equal("storage_uncertainty", recovered.Action); Assert.False(recovered.Complete);
            Assert.True(store.GetGate(m.ModelScopeKey).Blocked);
        }
        [Fact] public void FailedUiDecisionLeavesRulesAndGateUnchanged()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey); var incident = Incident(store, m.ModelScopeKey, op);
            var decision = Decision(incident, "create_rule"); decision.Rule = Rule(incident);
            using (var connection = new SqliteConnection("Data Source=" + Db + ";Pooling=False"))
            { connection.Open(); using (var tx = connection.BeginTransaction()) Assert.Equal("failed", store.ApplyDecision(decision).Persistence); }
            Assert.Empty(store.GetRules(m.ModelScopeKey)); Assert.True(store.GetGate(m.ModelScopeKey).Blocked);
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Db), "pending")));
        }
        [Fact] public void CorruptDatabaseDoesNotPretendClearOrDurableWhenFallbackAlsoUnwritable()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Db)); File.WriteAllText(Db, "corrupt database");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Db), "pending"), "blocks directory creation");
            var store = Store(); var op = new FailureOperation { ModelScopeKeys = new List<string> { "model" } };
            var result = store.SaveOperation(op); Assert.Equal("failed", result.Persistence); Assert.False(result.Durable);
            Assert.Throws<SqliteException>(() => new FailureLedgerStore(Db, true).GetGate("model"));
        }
        [Fact] public void OperationResultPersistsOnlyBoundedSafeReceipts()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey);
            op.FinalResult = new JObject { ["result"] = "raw code argument token", ["success"] = false,
                ["_failure"] = new JObject { ["path"] = @"C:\private\sample.rvt", ["description"] = @"C:\private\sample.rvt bearer secretabcd12345" } };
            op.UpdatedUtc = DateTimeOffset.UtcNow; Assert.True(store.SaveOperation(op).Recorded);
            var json = FailureJson.Serialize(store.GetOperation(op.OperationId));
            Assert.DoesNotContain("raw code argument", json); Assert.DoesNotContain("private", json); Assert.DoesNotContain("secretabcd12345", json);
        }
        [Fact] public void CompletedUnverifiedCapabilityDoesNotAloneLockButActualUncertaintyDoes()
        {
            var store = Store(); var m = Model(store);
            var op = new FailureOperation { ModelScopeKeys = new List<string> { m.ModelScopeKey }, Execution = "completed", Outcome = "unknown",
                MutationApplied = null, Complete = true, ObservationCoverage = "dynamic_code_unverified", RequiresReview = false };
            Assert.True(store.SaveOperation(op).Recorded); Assert.False(store.GetGate(m.ModelScopeKey).Blocked);
            op.RequiresReview = true; op.UpdatedUtc = DateTimeOffset.UtcNow; Assert.True(store.SaveOperation(op).Recorded);
            Assert.True(store.GetGate(m.ModelScopeKey).Blocked);
        }
        [Fact] public void CurrentVerifiedUiRevisionCanAuthorizeRetryAfterRollbackChangedRevision()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey); var incident = Incident(store, m.ModelScopeKey, op);
            var decision = Decision(incident, "accept_once"); decision.DocumentRevision = "revision_after_rollback";
            decision.Grant = new FailureGrant { ModelScopeKey = m.ModelScopeKey, DefinitionId = "warning_a", RevitYear = 2024, MaxCount = 1,
                ExistingElementIds = new List<long> { 10 }, RequestHash = op.RequestHash, DocumentRevision = decision.DocumentRevision };
            Assert.True(store.ApplyDecision(decision).Recorded);
            Assert.Equal("revision_after_rollback", store.GetGrants(m.ModelScopeKey).Single().DocumentRevision);
            Assert.Equal("revision_a", store.Query(new FailureRecordQuery { IncidentId = incident.IncidentId }).Records.Single().DocumentRevision);
        }
        [Fact] public void ObservedExactDialogMayCreateRuleButOtherYearsStayManual()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey, "committed");
            var incident = new FailureIncident { ModelScopeKey = m.ModelScopeKey, OperationId = op.OperationId, State = "observed", RevitYear = 2027,
                OperationContext = "open:ignore_unresolved_references", Messages = new List<FailureMessage> {
                    new FailureMessage { Severity = "TaskDialog", DefinitionId = "TaskDialog_Unresolved_References" } } };
            Assert.True(store.RecordIncident(incident).Recorded); var decision = Decision(incident, "create_rule");
            decision.Rule = new FailureRule { ModelScopeKey = m.ModelScopeKey, DefinitionId = incident.Messages[0].DefinitionId, Severity = "TaskDialog",
                RevitYear = 2027, OperationContext = incident.OperationContext, MaxCount = 1 };
            Assert.True(store.ApplyDecision(decision).Recorded);
            var accepted = new FailureIncident { ModelScopeKey = m.ModelScopeKey, OperationId = op.OperationId, State = "accepted_by_rule", RevitYear = 2027,
                OperationContext = incident.OperationContext, Messages = incident.Messages, RuleIds = new List<string> { decision.Rule.RuleId } };
            Assert.True(store.RecordIncident(accepted).Recorded);
            accepted.IncidentId = Guid.NewGuid().ToString("N"); accepted.RevitYear = 2024;
            Assert.False(store.RecordIncident(accepted).Recorded);
        }
        [Fact] public void NativeGrantMatchesActionContextHashRevisionAndConsumesOnce()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey, "cancelled");
            var incident = new FailureIncident { OperationId = op.OperationId, ModelScopeKey = m.ModelScopeKey, RevitYear = 2024,
                NativeAction = "sync", NativeContextHash = "context", RequestHash = op.RequestHash, DocumentRevision = op.DocumentRevision };
            Assert.True(store.RecordIncident(incident).Recorded);
            var decision = Decision(incident, "native_grant"); decision.Grant = new FailureGrant { ModelScopeKey = m.ModelScopeKey, RevitYear = 2024,
                NativeAction = "sync", NativeContextHash = "context", RequestHash = op.RequestHash, DocumentRevision = op.DocumentRevision };
            Assert.True(store.ApplyDecision(decision).Recorded); var grants = store.GetGrants(m.ModelScopeKey);
            Assert.NotNull(FailurePolicy.MatchNativeGrant(grants, m.ModelScopeKey, 2024, "sync", "context", op.RequestHash, op.DocumentRevision));
            Assert.Null(FailurePolicy.MatchNativeGrant(grants, m.ModelScopeKey, 2024, "save", "context", op.RequestHash, op.DocumentRevision));
            Assert.True(store.InvalidateGrants(m.ModelScopeKey).Recorded);
            Assert.Null(FailurePolicy.MatchNativeGrant(store.GetGrants(m.ModelScopeKey), m.ModelScopeKey, 2024, "sync", "context", op.RequestHash, op.DocumentRevision));
        }
        [Fact] public void FinalCompactionPreservesFailureIdentityActionGateAndPersistence()
        {
            var store = Store(); var m = Model(store); var op = Operation(store, m.ModelScopeKey);
            op.FinalResult = new JObject { ["success"] = false, ["_failure"] = new JObject {
                ["operationId"] = op.OperationId, ["incidentIds"] = new JArray("incident_id"), ["modelScopeKey"] = m.ModelScopeKey,
                ["action"] = "rolled_back", ["gate"] = "blocked", ["persistence"] = "recorded", ["complete"] = true,
                ["groups"] = new JArray(Enumerable.Range(0, 294).Select(i => new JObject { ["description"] = new string('界', 4096) })) } };
            op.UpdatedUtc = DateTimeOffset.UtcNow; Assert.True(store.SaveOperation(op).Recorded);
            var final = store.GetOperation(op.OperationId).FinalResult;
            Assert.Equal("incident_id", final["_failure"]["incidentIds"][0].Value<string>());
            Assert.Equal("blocked", final["_failure"].Value<string>("gate")); Assert.Equal("recorded", final["_failure"].Value<string>("persistence"));
            Assert.Equal("rolled_back", final["_failure"].Value<string>("action")); Assert.True(System.Text.Encoding.UTF8.GetByteCount(final.ToString()) <= 4096);
        }
        [Fact] public void OperationFallbackRecoveryCreatesReviewableIncidentAndDoesNotClaimFinalOutcome()
        {
            var store = Store(); var m = Model(store); var op = new FailureOperation { ModelScopeKeys = new List<string> { m.ModelScopeKey }, HostInstanceId = "host" };
            using (var connection = new SqliteConnection("Data Source=" + Db + ";Pooling=False"))
            { connection.Open(); using (var tx = connection.BeginTransaction()) Assert.Equal("fallback", store.SaveOperation(op).Persistence); }
            Assert.Equal(1, store.RecoverPending()); Assert.Equal("interrupted_unknown", store.GetOperation(op.OperationId).Execution);
            var incident = store.Query(new FailureRecordQuery { OperationId = op.OperationId }).Records.Single();
            Assert.Equal("open", incident.State); Assert.True(store.ApplyDecision(Decision(incident)).Recorded); Assert.False(store.GetGate(m.ModelScopeKey).Blocked);
        }
        [Fact] public void ProvenNormalSaveReplacementAliasPreservesGateButIndependentCopyDoesNot()
        {
            var store = Store(); var model = Model(store); var op = Operation(store, model.ModelScopeKey); Incident(store, model.ModelScopeKey, op);
            Assert.True(store.RecordPhysicalAlias(model.ModelScopeKey, "replaced_physical_file").Recorded);
            var replacement = Model(new FailureLedgerStore(Db), "replaced_physical_file", "path_a");
            Assert.Equal(model.ModelScopeKey, replacement.ModelScopeKey); Assert.True(store.GetGate(replacement.ModelScopeKey).Blocked);
            var independentCopy = Model(store, "copied_physical_file", "path_copy");
            Assert.NotEqual(model.ModelScopeKey, independentCopy.ModelScopeKey); Assert.False(store.GetGate(independentCopy.ModelScopeKey).Blocked);
        }
        [Fact] public void AtomicReservationPreventsTwoHostsStartingSameModelAndAllowsOtherModel()
        {
            var store = Store(); var a = Model(store); var b = Model(store, "physical_b");
            var first = new FailureOperation { HostInstanceId = "host_one", ModelScopeKeys = new List<string> { a.ModelScopeKey } };
            var second = new FailureOperation { HostInstanceId = "host_two", ModelScopeKeys = new List<string> { a.ModelScopeKey } };
            Assert.True(store.ReserveOperation(first).Recorded);
            Assert.Equal("REVIT_MODEL_BLOCKED", new FailureLedgerStore(Db).ReserveOperation(second).ErrorCode);
            Assert.Null(store.GetOperation(second.OperationId));
            second.ModelScopeKeys = new List<string> { b.ModelScopeKey }; Assert.True(store.ReserveOperation(second).Recorded);
            Assert.True(store.ReserveOperation(first).Recorded);
            first.ModelScopeKeys.Add(b.ModelScopeKey); Assert.Equal("REVIT_MODEL_BLOCKED", store.ReserveOperation(first).ErrorCode);
        }
        [Fact] public async System.Threading.Tasks.Task SimultaneousHostReservationsPermitExactlyOneWriter()
        {
            var store = Store(); var model = Model(store);
            using (var barrier = new System.Threading.Barrier(2))
            {
                var tasks = Enumerable.Range(1, 2).Select(n => System.Threading.Tasks.Task.Run(() => {
                    var contender = new FailureLedgerStore(Db);
                    var op = new FailureOperation { HostInstanceId = "host_" + n, ModelScopeKeys = new List<string> { model.ModelScopeKey } };
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                    return contender.ReserveOperation(op);
                })).ToArray();
                var results = await System.Threading.Tasks.Task.WhenAll(tasks);
                Assert.Single(results.Where(r => r.Recorded)); Assert.Single(results.Where(r => r.ErrorCode == "REVIT_MODEL_BLOCKED"));
            }
        }
        [Fact] public void FailedGrantInvalidationIsDurableAndRecoveredBeforeGateReopens()
        {
            var store = Store(); var model = Model(store); var op = Operation(store, model.ModelScopeKey); var incident = Incident(store, model.ModelScopeKey, op);
            var decision = Decision(incident, "accept_once"); decision.Grant = new FailureGrant { ModelScopeKey = model.ModelScopeKey,
                DefinitionId = "warning_a", RevitYear = 2024, MaxCount = 1, ExistingElementIds = new List<long> { 10 },
                RequestHash = op.RequestHash, DocumentRevision = op.DocumentRevision };
            Assert.True(store.ApplyDecision(decision).Recorded);
            using (var connection = new SqliteConnection("Data Source=" + Db + ";Pooling=False"))
            { connection.Open(); using (var tx = connection.BeginTransaction()) Assert.Equal("fallback", store.InvalidateGrants(model.ModelScopeKey).Persistence); }
            Assert.True(new FailureLedgerStore(Db, true).GetGate(model.ModelScopeKey).Blocked);
            Assert.Equal(1, store.RecoverPending()); Assert.False(store.GetGrants(model.ModelScopeKey).Single().Enabled);
            Assert.False(store.GetGate(model.ModelScopeKey).Blocked);
        }
        [Fact] public void MissingPhysicalIdentityCannotBypassKnownUnresolvedAliasOrInheritRules()
        {
            var store = Store(); var original = Model(store, "known_physical", "known_model_key");
            var op = Operation(store, original.ModelScopeKey); var accepted = Incident(store, original.ModelScopeKey, op);
            var createRule = Decision(accepted, "create_rule"); createRule.Rule = Rule(accepted); Assert.True(store.ApplyDecision(createRule).Recorded);
            var unresolved = Incident(store, original.ModelScopeKey, op);
            var unknown = store.ResolveModel(new FailureModelIdentity { ModelKey = "known_model_key", PhysicalKey = null,
                HostInstanceId = "new_host", LiveDocumentId = "new_doc", Title = "Sample" });
            Assert.NotEqual(original.ModelScopeKey, unknown.ModelScopeKey); Assert.True(unknown.IsTransient);
            Assert.Empty(store.GetRules(unknown.ModelScopeKey));
            var gate = new FailureLedgerStore(Db, true).GetGate(unknown.ModelScopeKey);
            Assert.True(gate.Blocked); Assert.Equal("FAILURE_IDENTITY_UNCERTAIN", gate.Reason); Assert.Contains(unresolved.IncidentId, gate.IncidentIds);
            var synthetic = store.Query(new FailureRecordQuery { ModelScopeKey = unknown.ModelScopeKey, IncludeDetails = true }).Records.Single();
            Assert.Equal("identity_uncertain", synthetic.Action); Assert.Contains(original.ModelScopeKey, synthetic.RelatedModelScopeKeys);
            Assert.True(store.ApplyDecision(Decision(synthetic)).Recorded);
            Assert.True(store.GetGate(unknown.ModelScopeKey).Blocked); // reviewing ambiguity alone cannot clear the original model issue
            Assert.Equal("REVIT_MODEL_BLOCKED", store.ReserveOperation(new FailureOperation { ModelScopeKeys = new List<string> { unknown.ModelScopeKey } }).ErrorCode);
            var provenCopy = Model(store, "independent_physical", "known_model_key");
            Assert.False(store.GetGate(provenCopy.ModelScopeKey).Blocked); Assert.Empty(store.GetRules(provenCopy.ModelScopeKey));
            Assert.True(store.ApplyDecision(Decision(unresolved)).Recorded);
            Assert.False(store.GetGate(unknown.ModelScopeKey).Blocked);
        }
        [Fact] public void UnknownAliasTracksFutureBlockersWhileNativeDetachedScopeStaysSeparate()
        {
            var store = Store(); var original = Model(store, "known_physical", "known_model_key");
            var unknown = store.ResolveModel(new FailureModelIdentity { ModelKey = "known_model_key", HostInstanceId = "host_new", LiveDocumentId = "doc_new" });
            Assert.False(store.GetGate(unknown.ModelScopeKey).Blocked);
            var op = Operation(store, original.ModelScopeKey); Incident(store, original.ModelScopeKey, op);
            Assert.True(store.GetGate(unknown.ModelScopeKey).Blocked);
            var detached = store.ResolveModel(new FailureModelIdentity { ModelKey = "known_model_key", IsTransient = true,
                HostInstanceId = "host_detached", LiveDocumentId = "doc_detached" });
            Assert.False(store.GetGate(detached.ModelScopeKey).Blocked);
        }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AmbiguousAndProvenFileReservationsExcludeEachOtherInBothOrders(bool ambiguousFirst)
        {
            var store = Store(); var proven = Model(store, "known_physical", "known_model_key");
            var ambiguous = store.ResolveModel(new FailureModelIdentity { ModelKey = "known_model_key", HostInstanceId = "ambiguous_host", LiveDocumentId = "ambiguous_doc" });
            var firstScope = ambiguousFirst ? ambiguous.ModelScopeKey : proven.ModelScopeKey;
            var secondScope = ambiguousFirst ? proven.ModelScopeKey : ambiguous.ModelScopeKey;
            var first = new FailureOperation { HostInstanceId = "first_host", ModelScopeKeys = new List<string> { firstScope } };
            var second = new FailureOperation { HostInstanceId = "second_host", ModelScopeKeys = new List<string> { secondScope } };
            Assert.True(store.ReserveOperation(first).Recorded);
            Assert.True(new FailureLedgerStore(Db, true).GetGate(secondScope).Blocked);
            Assert.Equal("REVIT_MODEL_BLOCKED", new FailureLedgerStore(Db).ReserveOperation(second).ErrorCode);
            Assert.Null(store.GetOperation(second.OperationId));
        }
        [Fact] public void ProvenScopeSeesActualAmbiguousFailureButNotDerivedIdentityExplanation()
        {
            var store = Store(); var proven = Model(store, "known_physical", "known_model_key");
            var originalOp = Operation(store, proven.ModelScopeKey); var originalIssue = Incident(store, proven.ModelScopeKey, originalOp);
            var ambiguous = store.ResolveModel(new FailureModelIdentity { ModelKey = "known_model_key", HostInstanceId = "new_host", LiveDocumentId = "new_doc" });
            Assert.True(store.ApplyDecision(Decision(originalIssue)).Recorded);
            Assert.False(store.GetGate(proven.ModelScopeKey).Blocked); // derived ambiguity does not feed back into its own candidate
            var realOp = Operation(store, ambiguous.ModelScopeKey); var realIssue = Incident(store, ambiguous.ModelScopeKey, realOp);
            Assert.True(store.GetGate(proven.ModelScopeKey).Blocked); Assert.Contains(realIssue.IncidentId, store.GetGate(proven.ModelScopeKey).IncidentIds);
            Assert.True(store.ApplyDecision(Decision(realIssue)).Recorded);
            Assert.False(store.GetGate(proven.ModelScopeKey).Blocked);
        }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AmbiguousSiblingReservationsConflictInEitherOrder(bool firstSiblingFirst)
        {
            var store = Store(); Model(store, "known_physical", "known_model_key");
            var q1 = store.ResolveModel(new FailureModelIdentity { ModelKey = "known_model_key", HostInstanceId = "host_q1", LiveDocumentId = "doc_q1" });
            var q2 = store.ResolveModel(new FailureModelIdentity { ModelKey = "known_model_key", HostInstanceId = "host_q2", LiveDocumentId = "doc_q2" });
            var first = new FailureOperation { ModelScopeKeys = new List<string> { firstSiblingFirst ? q1.ModelScopeKey : q2.ModelScopeKey } };
            var second = new FailureOperation { ModelScopeKeys = new List<string> { firstSiblingFirst ? q2.ModelScopeKey : q1.ModelScopeKey } };
            Assert.True(store.ReserveOperation(first).Recorded);
            Assert.True(new FailureLedgerStore(Db, true).GetGate(second.ModelScopeKeys[0]).Blocked);
            Assert.Equal("REVIT_MODEL_BLOCKED", new FailureLedgerStore(Db).ReserveOperation(second).ErrorCode);
        }
        [Fact] public void SharedAmbiguousCandidateDoesNotMergeProvenDistinctPhysicalFiles()
        {
            var store = Store(); var k1 = Model(store, "physical_one", "shared_alias"); var k2 = Model(store, "physical_two", "shared_alias");
            var unknown = store.ResolveModel(new FailureModelIdentity { ModelKey = "shared_alias", HostInstanceId = "unknown_host", LiveDocumentId = "unknown_doc" });
            Assert.True(store.ReserveOperation(new FailureOperation { ModelScopeKeys = new List<string> { k1.ModelScopeKey } }).Recorded);
            Assert.False(store.GetGate(k2.ModelScopeKey).Blocked);
            Assert.True(new FailureLedgerStore(Db).ReserveOperation(new FailureOperation { ModelScopeKeys = new List<string> { k2.ModelScopeKey } }).Recorded);
            Assert.True(store.GetGate(unknown.ModelScopeKey).Blocked);
            Assert.Equal("REVIT_MODEL_BLOCKED", store.ReserveOperation(new FailureOperation { ModelScopeKeys = new List<string> { unknown.ModelScopeKey } }).ErrorCode);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
