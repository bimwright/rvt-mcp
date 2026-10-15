using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin.Views;
using FailureMessage = RvtMcp.Failures.FailureMessage;

namespace RvtMcp.Plugin.Failures
{
    // Native objects never leave this UI-thread adapter. The ledger stores captured facts only.
    internal static partial class FailureControl
    {
        internal sealed class DocumentState
        {
            internal Document Document;
            internal string LiveId = Guid.NewGuid().ToString("N");
            internal FailureModel Model;
            internal long Revision;
            internal string Path;
            internal bool BaselineMeasured;
            internal bool ElementIndexComplete;
            internal HashSet<long> Elements = new HashSet<long>();
        }

        private static readonly Dictionary<Document, DocumentState> Documents = new Dictionary<Document, DocumentState>();
        private static readonly HashSet<string> UncertainModels = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, FailureIncident> NativeRequests = new Dictionary<string, FailureIncident>();
        private static readonly string SessionId = Guid.NewGuid().ToString("N");
        internal static readonly string HostInstanceId = ProcessIdentity(Process.GetCurrentProcess());
        internal static FailureLedgerStore Store { get; private set; }
        internal static Scope Active { get; private set; }
        private static FailureIssuesContext _uiContext = new FailureIssuesContext();
        private static string _revitUser;
        private static string _storageError;
        private static int Year { get { int.TryParse(AuthToken.RevitVersion, out var year); return year; } }
        public static bool IsStopped { get { return Active != null && Active.Stopped; } }

        private static string ProcessIdentity(Process process)
        {
            try { return process.Id + "-" + process.StartTime.ToUniversalTime().Ticks; }
            catch { return process.Id + "-unknown"; }
        }

        internal static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        public static void ThrowIfStopped()
        {
            if (IsStopped) throw new InvalidOperationException(Active.ErrorCode ?? "REVIT_FAILURE_ROLLED_BACK");
        }

        public static void NotifyBatchRolledBack()
        {
            if (Active != null) { Active.AnyCommitted = Active.GroupStates.Count > 0 && Active.GroupStates.Peek(); Active.BatchRolledBack = !Active.AnyCommitted; }
        }

        // Dynamic code that failed to compile never ran, so the operation did not start.
        public static void NotifyCompileFailed()
        {
            if (Active != null && Active.GroupStates.Count == 0 && !Active.AnyCommitted && !Active.NativeCommitted)
                Active.ExecutionStarted = false;
        }

        public static IDisposable BeginBatch() { return new BatchMarker(Active); }
        private sealed class BatchMarker : IDisposable
        {
            private readonly Scope _scope;
            internal BatchMarker(Scope scope) { _scope = scope; scope?.GroupStates.Push(scope.AnyCommitted); }
            public void Dispose() { if (_scope != null) _scope.GroupStates.Pop(); }
        }

        private static DocumentState State(Document doc, bool refresh = false)
        {
            if (doc == null || !doc.IsValidObject) return null;
            if (!Documents.TryGetValue(doc, out var state))
                Documents.Add(doc, state = new DocumentState { Document = doc });
            var path = doc.PathName ?? "";
            if (state.Model != null && !refresh && state.Path == path) return state;
            var identity = McpChangeTracker.Scope.Identity(doc);
            bool transient = doc.IsDetached || string.IsNullOrEmpty(path);
            var physical = transient ? null : HistoryFileIdentity.TryGet(path, App.DebugLog);
            var modelKey = transient ? null : identity?.Value<string>("key");
            state.Model = Store.ResolveModel(new FailureModelIdentity
            {
                ModelKey = modelKey, PhysicalKey = physical, Title = doc.Title,
                LiveDocumentId = state.LiveId, HostInstanceId = HostInstanceId,
                IsTransient = transient || string.IsNullOrEmpty(modelKey),
                KnownScopeKey = state.Path == path ? state.Model?.ModelScopeKey : null
            });
            state.Path = path;
            return state;
        }

        internal static void RefreshContext(UIApplication app, bool resolveIdentity = true)
        {
            try
            {
                _revitUser = app.Application.Username;
                var doc = app.ActiveUIDocument?.Document;
                DocumentState state = null;
                if (resolveIdentity) state = State(doc);
                else if (doc != null) Documents.TryGetValue(doc, out state);
                _uiContext = state == null ? new FailureIssuesContext() : new FailureIssuesContext
                {
                    IsAvailable = true, ModelScopeKey = state.Model.ModelScopeKey, ModelTitle = state.Model.Title,
                    RevitYear = Year, DocumentRevision = Revision(state), RevitUser = _revitUser, HostInstanceId = HostInstanceId
                };
            }
            catch { _uiContext = new FailureIssuesContext(); }
        }

        private static string Revision(DocumentState state) { return state.LiveId + ":" + state.Revision; }

        internal static FailureIssuesContext GetUiContext()
        {
            var value = _uiContext;
            return new FailureIssuesContext
            {
                IsAvailable = value.IsAvailable, ModelScopeKey = value.ModelScopeKey, ModelTitle = value.ModelTitle,
                RevitYear = value.RevitYear, DocumentRevision = value.DocumentRevision,
                RevitUser = value.RevitUser, HostInstanceId = value.HostInstanceId
            };
        }

        internal static FailureIssuesContext GetUiContext(string modelScopeKey)
        {
            var current = GetUiContext();
            if (current.ModelScopeKey == modelScopeKey) return current;
            var pending = NativeRequests.Values.Where(i => i.NativeAction == "open" && i.ModelScopeKey == modelScopeKey
                && i.CreatedUtc >= DateTimeOffset.UtcNow.AddMinutes(-5)).OrderByDescending(i => i.CreatedUtc).FirstOrDefault();
            return pending == null ? new FailureIssuesContext() : new FailureIssuesContext
            {
                IsAvailable = true, ModelScopeKey = pending.ModelScopeKey, ModelTitle = pending.ModelTitle,
                RevitYear = pending.RevitYear, DocumentRevision = pending.DocumentRevision,
                RevitUser = _revitUser, HostInstanceId = HostInstanceId
            };
        }

        internal static FailureGate GetUiGate(string modelScopeKey)
        {
            try
            {
                var gate = Store.GetGate(modelScopeKey);
                if (UncertainModels.Contains(modelScopeKey)) { gate.Blocked = true; gate.Reason = "FAILURE_STORAGE_FAILED"; }
                return gate;
            }
            catch { return new FailureGate { Blocked = true, Reason = "FAILURE_STORAGE_FAILED" }; }
        }

        internal static FailurePersistenceResult ApplyUiDecision(FailureDecision decision)
        {
            if (decision == null || Store == null || Active != null)
                return new FailurePersistenceResult { Persistence = "failed", ErrorCode = "REVIT_BUSY" };
            try
            {
                decision.Channel = "revit_ui";
                decision.WindowsUser = Environment.UserName;
                decision.RevitUser = _revitUser;
                decision.HostInstanceId = HostInstanceId;
                decision.DocumentRevision = GetUiContext(decision.ModelScopeKey).DocumentRevision;
                if (decision.ModelScopeKey != _uiContext.ModelScopeKey && decision.Action != "native_grant")
                    throw new InvalidOperationException("Activate this model in Revit before making an issue decision.");
                var grants = new List<FailureGrant>();
                if (decision.Grant != null) grants.Add(decision.Grant);
                if (decision.Grants != null) grants.AddRange(decision.Grants);
                foreach (var grant in grants)
                {
                    var live = Documents.Values.FirstOrDefault(d => d.Model?.ModelScopeKey == decision.ModelScopeKey);
                    if (grant.NativeAction == "open")
                    {
                        if (!NativeRequests.TryGetValue(grant.NativeContextHash ?? "", out var native)
                            || native.ModelScopeKey != decision.ModelScopeKey || native.RequestHash != grant.RequestHash
                            || native.CreatedUtc < DateTimeOffset.UtcNow.AddMinutes(-5))
                            throw new InvalidOperationException("Native open context expired; prepare a new bounded attempt.");
                    }
                    else if (live == null || Revision(live) != grant.DocumentRevision)
                        throw new InvalidOperationException("The model changed; inspect it and prepare a new attempt.");
                }
                var result = Store.ApplyDecision(decision);
                if (result.Recorded && decision.VerifiedModel) UncertainModels.Remove(decision.ModelScopeKey);
                return result;
            }
            catch { return new FailurePersistenceResult { Persistence = "failed", ErrorCode = "FAILURE_DECISION_REJECTED" }; }
        }

        internal static Scope Begin(UIApplication app, PendingRequest request)
        {
            if (Active != null) throw new InvalidOperationException("A failure scope is already active.");
            RefreshContext(app, !ToolReadPolicy.IsReadOnly(request.CommandName));
            var scope = new Scope(app, request);
            Active = scope;
            scope.Start();
            return scope;
        }

        internal sealed class Scope : IDisposable
        {
            internal readonly UIApplication Application;
            internal readonly PendingRequest Request;
            internal readonly PendingRequestLifecycle Lifecycle;
            internal readonly FailureOperation Operation;
            internal readonly List<FailureIncident> Incidents = new List<FailureIncident>();
            private readonly Dictionary<string, HashSet<long>> _before = new Dictionary<string, HashSet<long>>();
            private readonly HashSet<string> _completeBefore = new HashSet<string>();
            internal readonly bool IsRead;
            private readonly bool _opensModel;
            internal bool Stopped, Cancelled, BatchRolledBack, AnyCommitted, NativeCommitted, StorageFailed, Persisted, ExecutionStarted;
            internal string ErrorCode;
            internal string FailureModelKey;
            private readonly List<string> _gateIncidentIds = new List<string>();
            internal string OpenPath;
            internal string NativeOutcome;
            internal readonly Stack<bool> GroupStates = new Stack<bool>();
            internal readonly CommittedTransactionLog CommittedLog = new CommittedTransactionLog();
            internal readonly HashSet<string> OpeningPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // One entry per occurrence a rule answered: the same dialog can appear again in this operation and be closed by the user.
            internal readonly Dictionary<string, List<JObject>> DialogAnswers = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
            private string _userDialogKey;
            private int _capturedMessages, _capturedReferences, _capturedBytes;
            private readonly HashSet<string> _snapshotKeys = new HashSet<string>();
            private bool _finished;
            private int _cycle;

            internal Scope(UIApplication app, PendingRequest request)
            {
                Application = app; Request = request; Lifecycle = PendingRequestLifecycle.For(request.Tcs);
                IsRead = ToolReadPolicy.IsReadOnly(request.CommandName);
                _opensModel = request.CommandName == "open_model";
                Operation = new FailureOperation
                {
                    OperationId = Lifecycle.OperationId, SessionId = SessionId, HostInstanceId = HostInstanceId,
                    Tool = request.CommandName, RequestHash = Hash(request.CommandName + "\n" + Canonical(request.ParamsJson)),
                    RevitYear = Year,
                    ObservationCoverage = request.CommandName == "send_code_to_revit" || request.CommandName == "run_baked_tool"
                        ? "dynamic_code_unverified" : "supported_handlers_only"
                };
            }

            private static string Canonical(string json)
            {
                try { return CanonicalToken(JToken.Parse(json)).ToString(Formatting.None); } catch { return json; }
            }
            private static JToken CanonicalToken(JToken token)
            {
                if (token is JObject obj) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, CanonicalToken(p.Value))));
                if (token is JArray array) return new JArray(array.Select(CanonicalToken));
                return token.DeepClone();
            }

            internal void Start()
            {
                try { var current = Application.ActiveUIDocument?.Document; _userDialogKey = UserDialogBuffer.Key(current?.PathName, current?.Title); }
                catch { }
                if (Store == null || _storageError != null)
                {
                    if (!IsRead) Stop("FAILURE_STORAGE_FAILED", null);
                    return;
                }
                try
                {
                    DocumentState state = null;
                    var activeDocument = Application.ActiveUIDocument?.Document;
                    if (IsRead) { if (activeDocument != null) Documents.TryGetValue(activeDocument, out state); }
                    else state = State(activeDocument, refresh: true);
                    if (state != null)
                    {
                        Operation.ActiveModelBefore = state.Model.ModelScopeKey;
                        Operation.DocumentRevision = Revision(state);
                        if (!_opensModel) Attach(state);
                        // The active model is context, not a mutation target of typed open_model.
                        // Native opening validates and reserves the actual target before it opens.
                        if (!IsRead && !_opensModel)
                        {
                            var gate = Store.GetGate(state.Model.ModelScopeKey, Operation.OperationId);
                            if (gate.Blocked || UncertainModels.Contains(state.Model.ModelScopeKey))
                            { Stop("REVIT_MODEL_BLOCKED", state); return; }
                            var repeated = Store.GetRepeatedRequestIncidents(state.Model.ModelScopeKey, Operation.RequestHash);
                            if (repeated.Count > 0)
                            { Stop("REVIT_REQUEST_REPEATED", state); _gateIncidentIds.AddRange(repeated); return; }
                            Reserve();
                            if (!Stopped) IndexAndBaseline(state);
                        }
                    }
                    if (!IsRead && !Persisted && !Stopped) Reserve();
                }
                catch (Exception ex)
                {
                    App.DebugLog("Failure scope start failed for " + Request.CommandName + ": " + ex.GetType().Name + ": " + ex.Message);
                    StorageFailure();
                }
            }

            internal void Attach(DocumentState state)
            {
                ClaimModel(state?.Model);
            }

            internal void ClaimModel(FailureModel model)
            {
                if (model == null || Operation.ModelScopeKeys.Contains(model.ModelScopeKey)) return;
                Operation.ModelScopeKeys.Add(model.ModelScopeKey);
                // Another host must see this claim before a secondary model can commit.
                if (ExecutionStarted || Persisted) Reserve();
            }

            private void IndexAndBaseline(DocumentState state)
            {
                if (!state.BaselineMeasured)
                {
                    state.BaselineMeasured = true;
                    // Revit rejects an unfiltered collector; element instances OR types still covers every element.
                    var everything = new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true));
                    using (var collector = new FilteredElementCollector(state.Document).WherePasses(everything))
                    {
                        state.ElementIndexComplete = collector.GetElementCount() <= 200000;
                        if (state.ElementIndexComplete)
                            state.Elements = new HashSet<long>(collector.ToElementIds().Select(RevitCompat.GetId));
                    }
                    var warnings = state.Document.GetWarnings();
                    if (warnings.Count > 0)
                    {
                        var baseline = NewIncident(state, "baseline", "existing warnings", "observed", "baseline");
                        foreach (var warning in warnings.Take(5000))
                        {
                            var message = new FailureMessage { Source = "baseline", Severity = "Warning" };
                            try { message.DefinitionId = warning.GetFailureDefinitionId().Guid.ToString(); message.Description = Bounded(warning.GetDescriptionText());
                                message.FailingElementIds = warning.GetFailingElements().Select(RevitCompat.GetId).Take(Math.Max(0, 50000 - _capturedReferences)).ToList();
                                _capturedReferences += message.FailingElementIds.Count; }
                            catch { message.Complete = false; baseline.Complete = false; }
                            baseline.Messages.Add(message);
                            _capturedMessages++; _capturedBytes += Encoding.UTF8.GetByteCount(FailureJson.Serialize(message));
                            if (_capturedBytes > 30 * 1024 * 1024 || _capturedReferences >= 50000) { baseline.Complete = false; break; }
                        }
                        baseline.MessageCount = warnings.Count;
                        baseline.Complete &= warnings.Count <= 5000;
                        baseline.OmittedMessageCount = Math.Max(0, warnings.Count - baseline.Messages.Count);
                        Record(baseline);
                    }
                }
                _before[state.Model.ModelScopeKey] = new HashSet<long>(state.Elements);
                if (state.ElementIndexComplete) _completeBefore.Add(state.Model.ModelScopeKey);
            }

            internal FailureIncident NewIncident(DocumentState state, string source, string transaction, string status = "open", string action = "rolled_back")
            {
                return new FailureIncident
                {
                    OperationId = Operation.OperationId, ModelScopeKey = state?.Model.ModelScopeKey ?? Operation.ActiveModelBefore,
                    ModelTitle = state?.Model.Title, Source = source, TransactionName = Bounded(transaction),
                    RequestHash = Operation.RequestHash, DocumentRevision = state == null ? Operation.DocumentRevision : Revision(state),
                    RevitYear = Year, OperationContext = OpenPath == null ? "edit" : "open", State = status, Action = action
                };
            }

            internal void Record(FailureIncident incident, IEnumerable<string> grants = null)
            {
                if (!Persisted) SaveOperation();
                Incidents.Add(incident); Operation.IncidentIds.Add(incident.IncidentId);
                var persistence = Store.RecordIncident(incident, grants);
                incident.Persistence = persistence.Persistence;
                Persisted = true;
                if (!persistence.Recorded) StorageFailure();
            }

            internal void SaveOperation()
            {
                Operation.UpdatedUtc = DateTimeOffset.UtcNow;
                var persistence = Store.SaveOperation(Operation);
                Persisted = true;
                if (!persistence.Recorded) StorageFailure();
            }

            private void Reserve()
            {
                var reservation = Store.ReserveOperation(Operation);
                if (reservation.ErrorCode == "REVIT_MODEL_BLOCKED")
                {
                    Stopped = true; ErrorCode = "REVIT_MODEL_BLOCKED";
                    foreach (var key in Operation.ModelScopeKeys)
                    {
                        var gate = Store.GetGate(key, Operation.OperationId);
                        if (gate.Blocked) { FailureModelKey = key; _gateIncidentIds.AddRange(gate.IncidentIds); }
                    }
                    return;
                }
                Persisted = true;
                if (!reservation.Recorded) StorageFailure();
            }

            internal void StorageFailure()
            {
                StorageFailed = true; Stopped = true; ErrorCode = "FAILURE_STORAGE_FAILED";
                Operation.Complete = false;
                Operation.RequiresReview = true;
                foreach (var key in Operation.ModelScopeKeys) UncertainModels.Add(key);
            }

            internal void Stop(string code, DocumentState state)
            {
                Attach(state); Stopped = true; ErrorCode = code;
                FailureModelKey = FailureCapture.ReceiptModelScopeKey(state?.Model.ModelScopeKey ?? FailureModelKey,
                    Operation.Tool, Operation.ActiveModelBefore, Operation.ModelScopeKeys, null);
                if (code == "REVIT_MODEL_BLOCKED" && FailureModelKey != null)
                {
                    try { _gateIncidentIds.AddRange(Store.GetGate(FailureModelKey, Operation.OperationId).IncidentIds); } catch { StorageFailure(); }
                }
            }

            internal FailureProcessingResult Process(FailuresAccessor accessor, string source)
            {
                DocumentState state = null;
                try
                {
                    var failures = accessor.GetFailureMessages();
                    if (OpenPath != null && !Stopped && failures.Count == 0) return FailureProcessingResult.Continue;
                    state = State(accessor.GetDocument()); Attach(state);
                    if (Stopped) return Rollback(accessor);
                    if (state == null) { Stop("FAILURE_CAPTURE_INCOMPLETE", null); return Rollback(accessor); }
                    if (!IsRead && Store.GetGate(state.Model.ModelScopeKey, Operation.OperationId).Blocked)
                    { Stop("REVIT_MODEL_BLOCKED", state); return Rollback(accessor); }
                    if (failures.Count == 0) return FailureProcessingResult.Continue;
                    var incident = NewIncident(state, source, accessor.GetTransactionName());
                    incident.Severity = accessor.GetSeverity().ToString();
                    incident.MessageCount = failures.Count;
                    int remainingMessages = Math.Max(0, 5000 - _capturedMessages);
                    incident.Complete = failures.Count <= remainingMessages;
                    incident.OmittedMessageCount = Math.Max(0, failures.Count - remainingMessages);
                    var cycle = (++_cycle).ToString();
                    var refs = new HashSet<long>();
                    foreach (var failure in failures.Take(remainingMessages))
                    {
                        if (_capturedBytes >= 30 * 1024 * 1024) { incident.Complete = false; incident.OmittedMessageCount++; break; }
                        var message = new FailureMessage { Source = source, TransactionKey = incident.TransactionName,
                            Cycle = cycle, Sequence = incident.Messages.Count };
                        try { message.Severity = failure.GetSeverity().ToString(); } catch { message.Complete = false; }
                        try { message.DefinitionId = failure.GetFailureDefinitionId().Guid.ToString(); } catch { message.Complete = false; }
                        try { var description = failure.GetDescriptionText(); message.Description = Bounded(description); message.TextTruncated = description?.Length > 4096; message.Complete &= !message.TextTruncated; } catch { message.Complete = false; }
                        try { CaptureIds(failure.GetFailingElementIds(), message.FailingElementIds, refs, incident); } catch { message.Complete = false; }
                        try { CaptureIds(failure.GetAdditionalElementIds(), message.AdditionalElementIds, refs, incident); } catch { message.Complete = false; }
                        if (OpenPath != null)
                            message.ExistingElementIds = message.FailingElementIds.Concat(message.AdditionalElementIds).Distinct().ToList();
                        else if (_before.TryGetValue(state.Model.ModelScopeKey, out var existing) && _completeBefore.Contains(state.Model.ModelScopeKey))
                            message.ExistingElementIds = message.FailingElementIds.Concat(message.AdditionalElementIds).Where(existing.Contains).Distinct().ToList();
                        else
                        { message.ExistingElementIds = message.FailingElementIds.Concat(message.AdditionalElementIds).Distinct().ToList(); message.Complete = false; }
                        foreach (var id in message.FailingElementIds.Concat(message.AdditionalElementIds).Distinct())
                        {
                            if (_snapshotKeys.Count >= 200 || !_snapshotKeys.Add(state.Model.ModelScopeKey + ":" + id)) continue;
                            try
                            {
                                var element = state.Document.GetElement(RevitCompat.ToElementId(id));
                                XYZ position = (element?.Location as LocationPoint)?.Point;
                                message.Snapshots.Add(new FailureElementSnapshot { ElementId = id, Category = element?.Category?.Name,
                                    Type = element == null ? null : state.Document.GetElement(element.GetTypeId())?.Name,
                                    Level = element == null ? null : state.Document.GetElement(element.LevelId)?.Name,
                                    Position = position == null ? null : new[] { position.X, position.Y, position.Z },
                                    ExistsAfterRollback = null });
                            }
                            catch { message.Complete = false; }
                        }
                        incident.Complete &= message.Complete;
                        _capturedMessages++; _capturedBytes += Encoding.UTF8.GetByteCount(FailureJson.Serialize(message));
                        incident.Messages.Add(message);
                    }
                    bool openingWarnings = OpenPath != null && incident.Messages.All(m => m.Severity == "Warning") && incident.Complete;
                    var accumulated = Incidents.Where(i => i.ModelScopeKey == incident.ModelScopeKey && i.OperationContext == incident.OperationContext
                        && (i.State == "accepted_by_rule" || i.State == "accepted_once")).SelectMany(i => i.Messages).Concat(incident.Messages);
                    var policy = openingWarnings ? new FailurePolicyResult { Action = "continue", Reason = "opened_with_warnings" }
                        : FailurePolicy.Evaluate(accumulated, incident.Complete, state.Model.ModelScopeKey, Year,
                            incident.OperationContext, Operation.RequestHash, incident.DocumentRevision, Store.GetRules(state.Model.ModelScopeKey), Store.GetGrants(state.Model.ModelScopeKey));
                    incident.RuleIds = policy.RuleIds; incident.GrantIds = policy.GrantIds;
                    incident.State = policy.Action == "continue" ? (openingWarnings ? "observed" : policy.RuleIds.Count == 0 && policy.GrantIds.Count > 0 ? "accepted_once" : "accepted_by_rule") : "open";
                    incident.Action = policy.Action == "continue" ? (openingWarnings ? "opened_with_warnings" : "accepted_warning") : "rolled_back";
                    Record(incident, policy.GrantIds);
                    if (policy.Action != "continue" || StorageFailed)
                    { Stop(StorageFailed ? "FAILURE_STORAGE_FAILED" : !incident.Complete ? "FAILURE_CAPTURE_INCOMPLETE" : "REVIT_FAILURE_ROLLED_BACK", state); return Rollback(accessor); }
                    foreach (var failure in failures) if (failure.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(failure);
                    return FailureProcessingResult.Continue;
                }
                catch
                {
                    Stop("FAILURE_CAPTURE_INCOMPLETE", state); Operation.Complete = false;
                    if (state != null) UncertainModels.Add(state.Model.ModelScopeKey);
                    try { Record(NewIncident(state, "capture", "Failure capture incomplete", action: "capture_failed")); } catch { StorageFailure(); }
                    return Rollback(accessor);
                }
            }

            private void CaptureIds(ICollection<ElementId> ids, List<long> result, HashSet<long> all, FailureIncident incident)
            {
                foreach (var elementId in ids)
                {
                    var id = RevitCompat.GetId(elementId);
                    if (_capturedReferences >= 50000) { incident.Complete = false; incident.OmittedElementCount++; continue; }
                    _capturedReferences++; all.Add(id); result.Add(id);
                }
            }

            private FailureProcessingResult Rollback(FailuresAccessor accessor)
            {
                Stopped = true;
                accessor.SetFailureHandlingOptions(accessor.GetFailureHandlingOptions().SetClearAfterRollback(true));
                return FailureProcessingResult.ProceedWithRollBack;
            }

            internal void Complete(CommandResult result)
            {
                if (_finished) return;
                _finished = true;
                Operation.Execution = "completed";
                try
                {
                    var doc = Application.ActiveUIDocument?.Document;
                    DocumentState state = null;
                    if (IsRead) { if (doc != null) Documents.TryGetValue(doc, out state); }
                    else state = State(doc);
                    Operation.ActiveModelAfter = state?.Model.ModelScopeKey;
                }
                catch { Operation.Complete = false; }
                FailureCompletion.Apply(Operation, new FailureCompletionFacts
                {
                    ExecutionStarted = ExecutionStarted, HandlerSucceeded = result?.Success == true,
                    IsReadOnly = IsRead, HasPersistedRecord = Persisted, HasIncidents = Incidents.Count > 0,
                    PolicyStopped = Stopped, NativeCancelled = Cancelled, GroupRolledBack = BatchRolledBack,
                    AnyCommittedChanges = AnyCommitted, NativeCommitted = NativeCommitted,
                    StorageFailed = StorageFailed, NativeOutcomeUnknown = NativeOutcome == "unknown"
                });
                if (Operation.RequiresReview)
                {
                    foreach (var key in Operation.ModelScopeKeys.Where(k => !Incidents.Any(i => i.ModelScopeKey == k && i.State == "open")).ToList())
                    {
                        var state = Documents.Values.FirstOrDefault(d => d.Model?.ModelScopeKey == key);
                        var incident = NewIncident(state, "outcome", "Operation outcome requires review", action: "outcome_unknown");
                        incident.ModelScopeKey = key; incident.Complete = false;
                        incident.NativeContextDescription = "The operation did not establish a complete native outcome. Inspect the model before closing this issue. " + Bounded(result?.Error);
                        try { Record(incident); } catch { StorageFailure(); }
                    }
                }
                if (Stopped && result != null) { result.Success = false; result.Error = ErrorCode ?? "REVIT_FAILURE_ROLLED_BACK"; }
            }

            internal void Enrich(JObject envelope)
            {
                // Existing model warnings stay in the ledger but are not a failure of this call.
                var baselineIds = new HashSet<string>(Incidents.Where(i => i.Source == "baseline").Select(i => i.IncidentId));
                var own = Incidents.Where(i => i.Source != "baseline").ToList();
                if (own.Count > 0 || Stopped)
                {
                    var messages = own.SelectMany(i => i.Messages).ToList();
                    // Only an uncertain model is gated; a clean Revit rollback or a refused repeat leaves it writable.
                    bool blocked = Operation.RequiresReview || StorageFailed
                        || ErrorCode == "REVIT_MODEL_BLOCKED" || ErrorCode == "FAILURE_STORAGE_FAILED" || ErrorCode == "FAILURE_CAPTURE_INCOMPLETE"
                        || own.Any(i => i.State == "open" && !((i.Action == "rolled_back" || i.Action == "cancelled") && i.Complete));
                    var groups = new JArray(messages.GroupBy(m => new { m.DefinitionId, m.Severity }).Take(20).Select(g => new JObject
                    { ["failureDefinitionId"] = g.Key.DefinitionId, ["severity"] = g.Key.Severity, ["count"] = g.Count(),
                        ["description"] = (g.First().Description ?? "").Substring(0, Math.Min(320, (g.First().Description ?? "").Length)),
                        ["sampleElementIds"] = new JArray(g.SelectMany(m => m.FailingElementIds).Distinct().Take(20)), ["resolutionsApplied"] = false }));
                    foreach (var group in groups.OfType<JObject>()) DescribeDialog(group);
                    envelope["_failure"] = new JObject
                    {
                        ["schemaVersion"] = 1, ["operationId"] = Operation.OperationId,
                        ["incidentIds"] = new JArray(Operation.IncidentIds.Where(id => !baselineIds.Contains(id)).Concat(_gateIncidentIds).Distinct().Take(100)),
                        ["modelScopeKey"] = FailureCapture.ReceiptModelScopeKey(FailureModelKey, Operation.Tool,
                            Operation.ActiveModelBefore, Operation.ModelScopeKeys, own.Select(i => i.ModelScopeKey)),
                        ["action"] = Operation.Outcome, ["mutationApplied"] = Operation.MutationApplied,
                        ["gate"] = blocked ? "blocked" : "open",
                        ["persistence"] = StorageFailed ? "failed" : "recorded",
                        ["complete"] = Operation.Complete && own.All(i => i.Complete),
                        ["messageCount"] = own.Sum(i => i.MessageCount), ["observationCoverage"] = Operation.ObservationCoverage,
                        ["groups"] = groups,
                        ["nextStep"] = FailureCapture.NextStep(Stopped ? ErrorCode ?? "REVIT_FAILURE_ROLLED_BACK" : null, blocked,
                            own.Any(i => i.State == "accepted_by_rule" || i.State == "accepted_once"))
                    };
                }
                if (!IsRead || Persisted || Stopped)
                    envelope["_operation"] = new JObject { ["operationId"] = Operation.OperationId,
                        ["hostInstanceId"] = HostInstanceId,
                        ["execution"] = Operation.Execution, ["outcome"] = Operation.Outcome, ["mutationApplied"] = Operation.MutationApplied,
                        ["activeModelBefore"] = Operation.ActiveModelBefore, ["activeModelAfter"] = Operation.ActiveModelAfter,
                        ["complete"] = Operation.Complete, ["requiresReview"] = Operation.RequiresReview,
                        ["observationCoverage"] = Operation.ObservationCoverage };
                if (baselineIds.Count > 0 && envelope["_operation"] is JObject operation)
                    operation["baselineIncidentId"] = baselineIds.First();
                // An error without a receipt reaches the agent as plain text, so the report would be lost: keep it for the next call.
                if (_userDialogKey != null && envelope["_userDialogs"] == null
                    && (envelope.Value<bool?>("success") == true || envelope["_operation"] != null))
                {
                    var report = UserDialogs.Take(_userDialogKey);
                    if (report != null) envelope["_userDialogs"] = report;
                }
                SafetyResponseMetadata.Attach(envelope, envelope);
            }

            // Dialogs carry what the catalogue knows about them and who closed them; Revit does not say which button the user chose.
            private void DescribeDialog(JObject group)
            {
                var severity = group.Value<string>("severity");
                if (severity != "TaskDialog" && severity != "Dialog") return;
                var code = group.Value<string>("failureDefinitionId") ?? "";
                var entry = RevitMessageCatalog.FindDialog(code);
                if (entry?.Title != null) group["title"] = FailureCapture.SafeText(entry.Title, RevitMessageLookup.MaxTitle);
                if (entry?.Buttons.Count > 0) group["buttons"] = RevitMessageLookup.Buttons(entry);
                if (!string.IsNullOrEmpty(entry?.CommonButtons) && entry.CommonButtons != "None") group["commonButtons"] = entry.CommonButtons;
                FailureCapture.DescribeAnswers(group, DialogAnswers.TryGetValue(code, out var answers) ? answers : null);
            }

            internal void PersistFinal(JObject envelope)
            {
                if (IsRead && !Persisted && Incidents.Count == 0) return;
                var final = (JObject)envelope.DeepClone();
                if (final["changes"] != null) final["_changes"] = final["changes"].DeepClone();
                if (final["history_transfer"] is JObject marker)
                    final["_history"] = new JObject { ["status"] = marker.Value<string>("status") ?? "pending_transfer",
                        ["callId"] = Request.History?.Id, ["complete"] = false };
                final.Remove("history_transfer");
                if (Encoding.UTF8.GetByteCount(final.ToString(Formatting.None)) > 32768)
                    final["data"] = new JObject { ["omitted"] = true, ["reason"] = "Bounded durable operation result; inspect the original response." };
                Operation.FinalResult = final;
                try { SaveOperation(); } catch { StorageFailure(); }
                if (StorageFailed)
                { envelope["success"] = false; envelope["error"] = "FAILURE_STORAGE_FAILED"; Enrich(envelope); }
            }

            public void Dispose()
            {
                if (!_finished) Complete(null);
                if (ReferenceEquals(Active, this)) Active = null;
                RefreshContext(Application, !IsRead);
            }
        }

        private static string Bounded(string value)
        {
            return value == null ? null : FailureCapture.SafeText(value);
        }
    }
}
