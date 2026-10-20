using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using FailureMessage = RvtMcp.Failures.FailureMessage;

namespace RvtMcp.Plugin.Failures
{
    internal static partial class FailureControl
    {
        private sealed class OpenAttempt
        {
            internal string Path, Revision;
            internal FailureModel Model;
        }

        private sealed class SaveCorrelation
        {
            internal string OperationId, ModelScopeKey;
            internal DateTimeOffset Time;
        }

        private sealed class SaveIdentity
        {
            internal string Path, Scope;
        }

        private static bool _subscribed, _applicationClosing;
        private static string _nativeOperation;
        private static readonly List<OpenAttempt> Opening = new List<OpenAttempt>();
        private static OpenAttempt _lastOpened;
        private static readonly Dictionary<Document, string> SyncAuthorities = new Dictionary<Document, string>();
        private static readonly Dictionary<int, DocumentState> Closing = new Dictionary<int, DocumentState>();
        private static readonly Dictionary<Document, SaveIdentity> Saving = new Dictionary<Document, SaveIdentity>();
        private static SaveCorrelation _blockedSave;
        // Paths the user is opening by hand; dialogs shown meanwhile belong to that model, not the active one.
        private static readonly List<string> ManualOpening = new List<string>();
        internal static readonly UserDialogBuffer UserDialogs = new UserDialogBuffer();
        // Set by revit_show_message around its own dialog: that dialog is the agent's message, not a Revit condition.
        internal static bool OwnDialogPending;

        internal static void Initialize(UIControlledApplication application)
        {
            if (_subscribed) return;
            Store = new FailureLedgerStore();
            FailureLedgerStore.Diagnostic = App.DebugLog;
            try
            {
                _ledgerResetGeneration = Store.RefreshResetState();
                Store.Initialize();
                Store.RecoverPending();
                var live = new HashSet<string>(StringComparer.Ordinal) { HostInstanceId };
                bool hostEvidenceComplete = true;
                foreach (var process in Process.GetProcessesByName("Revit"))
                    using (process)
                    {
                        var identity = ProcessIdentity(process);
                        if (identity.EndsWith("-unknown", StringComparison.Ordinal)) hostEvidenceComplete = false;
                        live.Add(identity);
                    }
                // Inaccessible process metadata is not evidence that another host has stopped.
                if (hostEvidenceComplete) Store.MarkInterruptedHosts(live);
            }
            catch (Exception ex)
            {
                _storageError = "FAILURE_STORAGE_FAILED";
                App.DebugLog("Failure ledger initialization failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            var controlled = application.ControlledApplication;
            controlled.FailuresProcessing += OnFailuresProcessing;
            controlled.DocumentChanged += OnDocumentChanged;
            controlled.DocumentOpening += OnDocumentOpening;
            controlled.DocumentOpened += OnDocumentOpened;
            controlled.DocumentSaving += OnDocumentSaving;
            controlled.DocumentSaved += OnDocumentSaved;
            controlled.DocumentSavingAs += OnDocumentSavingAs;
            controlled.DocumentSavedAs += OnDocumentSavedAs;
            controlled.DocumentSynchronizingWithCentral += OnSynchronizing;
            controlled.DocumentSynchronizedWithCentral += OnSynchronized;
            controlled.DocumentClosing += OnDocumentClosing;
            controlled.DocumentClosed += OnDocumentClosed;
            application.DialogBoxShowing += OnDialogBoxShowing;
            application.ViewActivated += OnViewActivated;
            application.ApplicationClosing += OnApplicationClosing;
            _subscribed = true;
        }

        internal static void Shutdown(UIControlledApplication application)
        {
            if (!_subscribed) return;
            _applicationClosing = true;
            var controlled = application.ControlledApplication;
            controlled.FailuresProcessing -= OnFailuresProcessing;
            controlled.DocumentChanged -= OnDocumentChanged;
            controlled.DocumentOpening -= OnDocumentOpening;
            controlled.DocumentOpened -= OnDocumentOpened;
            controlled.DocumentSaving -= OnDocumentSaving;
            controlled.DocumentSaved -= OnDocumentSaved;
            controlled.DocumentSavingAs -= OnDocumentSavingAs;
            controlled.DocumentSavedAs -= OnDocumentSavedAs;
            controlled.DocumentSynchronizingWithCentral -= OnSynchronizing;
            controlled.DocumentSynchronizedWithCentral -= OnSynchronized;
            controlled.DocumentClosing -= OnDocumentClosing;
            controlled.DocumentClosed -= OnDocumentClosed;
            application.DialogBoxShowing -= OnDialogBoxShowing;
            application.ViewActivated -= OnViewActivated;
            application.ApplicationClosing -= OnApplicationClosing;
            _subscribed = false;
            PendingRequestLifecycle.SetHostBusy(false);
        }

        internal static void OnIdling(UIApplication application)
        {
            // Readiness is only a host signal. It never completes an in-flight operation.
            PendingRequestLifecycle.SetHostBusy(false);
            _applicationClosing = false; // A cancelled application exit has returned to a live idle UI.
            ManualOpening.Clear(); // Opening is modal; an idle UI has none in progress.
            RevitMessageCatalog.EnsureLoaded(application);
            if (application != null) RefreshContext(application);
            if (_blockedSave != null && DateTimeOffset.UtcNow - _blockedSave.Time > TimeSpan.FromMinutes(2))
                _blockedSave = null;
            if (Active == null)
            {
                Opening.Clear(); _lastOpened = null; SyncAuthorities.Clear(); _nativeOperation = null;
            }
        }

        private static void OnApplicationClosing(object sender, ApplicationClosingEventArgs args)
        {
            // Application shutdown and independent human operations are never intercepted.
            _applicationClosing = true;
        }

        private static bool Scoped()
        {
            if (Active == null || _applicationClosing) return false;
            if (_nativeOperation != Active.Operation.OperationId)
            {
                _nativeOperation = Active.Operation.OperationId;
                Opening.Clear(); _lastOpened = null; SyncAuthorities.Clear();
            }
            return true;
        }

        private static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs args)
        {
            if (!Scoped()) { RecordUserFailures(args); return; }
            try { args.SetProcessingResult(Active.Process(args.GetFailuresAccessor(), "global")); }
            catch
            {
                Active.Stop("FAILURE_CAPTURE_INCOMPLETE", null);
                Active.Operation.Complete = false;
                try
                {
                    var accessor = args.GetFailuresAccessor();
                    accessor.SetFailureHandlingOptions(accessor.GetFailureHandlingOptions().SetClearAfterRollback(true));
                    args.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
                }
                catch { Active.NativeOutcome = "unknown"; }
            }
        }

        private static void Invalidate(DocumentState state)
        {
            if (state?.Model == null) return;
            state.Revision++;
            try
            {
                if (!Store.InvalidateGrants(state.Model.ModelScopeKey).Recorded)
                {
                    UncertainModels.Add(state.Model.ModelScopeKey);
                    Active?.StorageFailure();
                }
            }
            catch { UncertainModels.Add(state.Model.ModelScopeKey); Active?.StorageFailure(); }
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs args)
        {
            // Outside MCP, only identity/revision/index maintenance is observed, never failure contents.
            try
            {
                var document = args.GetDocument();
                var state = State(document);
                if (state == null || args.Operation == UndoOperation.TransactionRolledBack) return;
                var added = args.GetAddedElementIds();
                var deleted = args.GetDeletedElementIds();
                var modified = args.GetModifiedElementIds();
                if (state.ElementIndexComplete)
                {
                    foreach (var id in deleted) state.Elements.Remove(RevitCompat.GetId(id));
                    foreach (var id in added) state.Elements.Add(RevitCompat.GetId(id));
                    if (state.Elements.Count > 200000) { state.Elements.Clear(); state.ElementIndexComplete = false; }
                }
                Invalidate(state);
                if (!Scoped()) return;
                Active.Attach(state);
                bool changed = added.Count != 0 || deleted.Count != 0 || modified.Count != 0;
                if (Active.OpenPath == null && changed)
                {
                    _lastOpened = null;
                    if (args.Operation == UndoOperation.TransactionCommitted || args.Operation == UndoOperation.TransactionRedone)
                    { Active.AnyCommitted = true; Active.BatchRolledBack = false; Active.CommittedLog.Committed(args.GetTransactionNames()); }
                    else if (args.Operation == UndoOperation.TransactionUndone
                        || (args.Operation == UndoOperation.TransactionGroupRolledBack && Active.GroupStates.Count == 0))
                    { Active.NativeOutcome = "unknown"; Active.Operation.Complete = false; }
                }
                // A rolled-back group outside a batch reports no element changes, only the names it reverted.
                else if (Active.OpenPath == null && args.Operation == UndoOperation.TransactionGroupRolledBack && Active.GroupStates.Count == 0)
                {
                    if (Active.CommittedLog.TryRevert(args.GetTransactionNames())) Active.AnyCommitted = Active.CommittedLog.Count > 0;
                    else { Active.NativeOutcome = "unknown"; Active.Operation.Complete = false; }
                }
            }
            catch { if (Active != null) { Active.Operation.Complete = false; Active.NativeOutcome = "unknown"; } }
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs args)
        {
            try
            {
                var previous = args.PreviousActiveView?.Document;
                var current = args.CurrentActiveView?.Document;
                if (previous != null && previous.IsValidObject) Invalidate(State(previous));
                if (current != null && current.IsValidObject && current != previous) Invalidate(State(current));
                // A pending unopened-file approval is also tied to its prepared UI context.
                foreach (var request in NativeRequests.Values.ToList())
                    if (request.NativeAction == "open") Store.InvalidateGrants(request.ModelScopeKey);
                NativeRequests.Clear();
                var application = sender as UIApplication;
                if (application != null) RefreshContext(application);
            }
            catch { if (Active != null) Active.StorageFailure(); }
        }

        private static string FileRevision(string path, string physical, string format)
        {
            var file = new FileInfo(path);
            return Hash("file:" + physical + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks + ":" + format);
        }

        private static FailureModel FileModel(string path, string identityPath, bool workshared)
        {
            var physical = HistoryFileIdentity.TryGet(path);
            var logical = ChangeHistoryIdentity.Create(identityPath, workshared ? "central" : "file", Path.GetFileNameWithoutExtension(path));
            return Store.ResolveModel(new FailureModelIdentity
            {
                PhysicalKey = physical, ModelKey = logical?.Value<string>("key"),
                Title = Path.GetFileNameWithoutExtension(path), HostInstanceId = HostInstanceId,
                IsFamilyDocument = string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase),
                LiveDocumentId = Hash("unopened:" + path.ToUpperInvariant()), IsTransient = physical == null
            });
        }

        private static void OnDocumentOpening(object sender, DocumentOpeningEventArgs args)
        {
            if (!Scoped()) { ManualOpening.Add(args.PathName ?? ""); return; }
            try
            {
                var path = args.PathName;
                using (var header = BasicFileInfo.Extract(path))
                {
                    var identityPath = header.IsWorkshared && !string.IsNullOrWhiteSpace(header.CentralPath) ? header.CentralPath : path;
                    var model = FileModel(path, identityPath, header.IsWorkshared);
                    var physical = HistoryFileIdentity.TryGet(path);
                    var revision = FileRevision(path, physical, header.Format);
                    bool risk = header.IsWorkshared && (header.IsCentral || !header.IsSavedInCurrentVersion);
                    var context = Hash("open|" + physical + "|" + path.ToUpperInvariant() + "|" + header.Format
                        + "|central=" + header.IsCentral + "|workshared=" + header.IsWorkshared + "|revision=" + revision);
                    var description = "Open " + (header.IsCentral ? "central" : header.IsWorkshared ? "workshared" : "non-workshared")
                        + " model; saved format " + header.Format + "; target Revit " + Year + ". File identity and current header are bound to this attempt.";
                    if (!Guard(args, null, model, "open", context, revision, description, risk, physical != null)) return;
                    var attempt = new OpenAttempt { Path = path, Model = model, Revision = revision };
                    Opening.Add(attempt); Active.OpenPath = path; Active.OpeningPaths.Add(path);
                }
            }
            catch
            {
                FailureModel model = null;
                try { model = FileModel(args.PathName, args.PathName, false); } catch { Active.StorageFailure(); }
                DenyIncomplete(args, null, "open", "Opening could not be classified from the native file header. Open it manually in Revit.", model);
            }
        }

        private static void OnDocumentOpened(object sender, DocumentOpenedEventArgs args)
        {
            if (!Scoped()) { ManualOpened(args); return; }
            try
            {
                var attempt = Opening.Count == 0 ? null : Opening[Opening.Count - 1];
                if (Opening.Count > 0) Opening.RemoveAt(Opening.Count - 1);
                if (attempt != null) Active.OpeningPaths.Remove(attempt.Path);
                Active.OpenPath = Opening.Count == 0 ? null : Opening[Opening.Count - 1].Path;
                if (args.Status == RevitAPIEventStatus.Succeeded && args.Document != null)
                {
                    var state = State(args.Document, true);
                    Active.Attach(state);
                    _lastOpened = new OpenAttempt { Path = args.Document.PathName, Model = state.Model, Revision = Revision(state) };
                    Invalidate(state);
                    _lastOpened.Revision = Revision(state);
                    Active.SaveOperation();
                }
                else ObserveCancelledOrFailed(args.Status, "open", null, attempt?.Model);
            }
            catch { Active.NativeOutcome = "unknown"; Active.Operation.Complete = false; }
        }

        private static void OnDocumentSaving(object sender, DocumentSavingEventArgs args)
        {
            DocumentState state = null;
            try
            {
                state = State(args.Document);
                Saving[args.Document] = new SaveIdentity { Path = args.Document.PathName, Scope = state.Model.ModelScopeKey };
                if (!Scoped()) return;
                bool risk = false; string format = Year.ToString(); string physical = null;
                var path = args.Document.PathName;
                if (args.Document.IsWorkshared)
                {
                    using (var header = BasicFileInfo.Extract(path))
                    {
                        format = header.Format;
                        risk = header.IsCentral || !header.IsSavedInCurrentVersion;
                        physical = HistoryFileIdentity.TryGet(path);
                    }
                }
                bool partOfApprovedSync = SyncAuthorities.TryGetValue(args.Document, out var operation) && operation == Active.Operation.OperationId;
                var context = Hash("save|" + state.Model.ModelScopeKey + "|" + path.ToUpperInvariant() + "|" + physical + "|" + format + "|" + Revision(state));
                Guard(args, state, state.Model, "save", context, Revision(state),
                    "Save the currently open model; stored format " + format + "; target Revit " + Year + "; central/upgrade=" + risk + ".",
                    risk && !partOfApprovedSync, !args.Document.IsWorkshared || physical != null);
            }
            catch { if (Scoped()) DenyIncomplete(args, state, "save", "Save identity or format could not be verified; use Revit to inspect and save the model."); }
        }

        private static void OnDocumentSavingAs(object sender, DocumentSavingAsEventArgs args)
        {
            if (!Scoped()) return;
            DocumentState state = null;
            try
            {
                state = State(args.Document);
                var target = args.PathName;
                bool exists = File.Exists(target);
                string physical = exists ? HistoryFileIdentity.TryGet(target) : null;
                string format = null;
                if (exists) using (var header = BasicFileInfo.Extract(target)) format = header.Format;
                bool risk = args.IsSavingAsCentralFile || exists || args.Document.IsWorkshared;
                var context = Hash("save_as|" + state.Model.ModelScopeKey + "|" + target.ToUpperInvariant()
                    + "|central=" + args.IsSavingAsCentralFile + "|overwrite=" + exists + "|" + physical + "|" + format + "|" + Revision(state));
                Guard(args, state, state.Model, "save_as", context, Revision(state),
                    "Save As; central=" + args.IsSavingAsCentralFile + "; overwrite existing file=" + exists
                    + "; existing format=" + (format ?? "new file") + "; target Revit " + Year + ". The observed target and central option are bound to this attempt.",
                    risk, !string.IsNullOrWhiteSpace(target) && Path.IsPathRooted(target) && (!exists || physical != null));
            }
            catch { DenyIncomplete(args, state, "save_as", "Save As target or native options could not be verified; perform this action manually in Revit."); }
        }

        private static void OnSynchronizing(object sender, DocumentSynchronizingWithCentralEventArgs args)
        {
            if (!Scoped()) return;
            DocumentState state = null;
            try
            {
                state = State(args.Document);
                var options = args.Options;
                string centralVersion = null, centralPhysical = null, centralRevision = null;
                if (File.Exists(args.Location))
                {
                    using (var header = BasicFileInfo.Extract(args.Location))
                    {
                        centralVersion = header.Format;
                        centralPhysical = HistoryFileIdentity.TryGet(args.Location);
                        centralRevision = FileRevision(args.Location, centralPhysical, centralVersion);
                    }
                }
                var context = Hash("synchronize|" + state.Model.ModelScopeKey + "|" + args.Location + "|" + Revision(state)
                    + "|central=" + centralPhysical + "|format=" + centralVersion + "|fileRevision=" + centralRevision
                    + "|" + options.SaveLocalBefore + "|" + options.SaveLocalAfter + "|" + options.Compact
                    + "|" + options.RelinquishBorrowedElements + "|" + options.RelinquishFamilyWorksets
                    + "|" + options.RelinquishProjectStandardWorksets + "|" + options.RelinquishUserCreatedWorksets
                    + "|" + options.RelinquishViewWorksets + "|cloud_option=not_exposed|" + Hash(args.Comments));
                var description = "Synchronize with central; save local before=" + options.SaveLocalBefore + "; save local after=" + options.SaveLocalAfter
                    + "; compact=" + options.Compact + "; relinquish borrowed elements=" + options.RelinquishBorrowedElements
                    + "; relinquish family/standard/user/view worksets=" + options.RelinquishFamilyWorksets + "/"
                    + options.RelinquishProjectStandardWorksets + "/" + options.RelinquishUserCreatedWorksets + "/" + options.RelinquishViewWorksets
                    + "; central format=" + (centralVersion ?? "unverified")
                    + ". Central identity, header, comment fingerprint and native options are bound to this attempt.";
                if (Guard(args, state, state.Model, "synchronize", context, Revision(state), description, true,
                    centralPhysical != null && centralRevision != null)) SyncAuthorities[args.Document] = Active.Operation.OperationId;
            }
            catch { DenyIncomplete(args, state, "synchronize", "Synchronization native options could not be verified; synchronize manually in Revit."); }
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs args)
        {
            DocumentState state = null;
            try
            {
                state = State(args.Document);
                if (Scoped())
                {
                    if (args.Document.IsModified)
                    {
                        // The event exposes neither Close(false) nor save-on-close options.
                        DenyIncomplete(args, state, "close", "Manual-only: this modified model's Closing event does not expose save-on-close versus discard. Close it through Revit.");
                        return;
                    }
                    if (!Guard(args, state, state.Model, "close", Hash("close|" + state.Model.ModelScopeKey + "|" + Revision(state)),
                        Revision(state), "Close the unmodified model; no save or discard is requested by this handler.", false, true)) return;
                }
                if (state != null) { Invalidate(state); Closing[args.DocumentId] = state; }
            }
            catch { if (Scoped()) DenyIncomplete(args, state, "close", "Closing context could not be verified; close the model manually in Revit."); }
        }

        private static void OnDocumentClosed(object sender, DocumentClosedEventArgs args)
        {
            if (Closing.TryGetValue(args.DocumentId, out var state))
            {
                Closing.Remove(args.DocumentId);
                if (args.Status == RevitAPIEventStatus.Succeeded)
                {
                    Documents.Remove(state.Document);
                    Saving.Remove(state.Document);
                    SyncAuthorities.Remove(state.Document);
                }
            }
            if (Scoped()) ObserveCancelledOrFailed(args.Status, "close", null);
        }

        private static void OnDocumentSaved(object sender, DocumentSavedEventArgs args)
        {
            try
            {
                if (args.Document != null && Saving.TryGetValue(args.Document, out var before))
                {
                    Saving.Remove(args.Document);
                    if (args.Status == RevitAPIEventStatus.Succeeded
                        && Handlers.OpenModelLocalPolicy.PathsEqual(before.Path, args.Document.PathName))
                    {
                        var physical = HistoryFileIdentity.TryGet(args.Document.PathName);
                        if (physical == null || !Store.RecordPhysicalAlias(before.Scope, physical).Recorded)
                        { UncertainModels.Add(before.Scope); Active?.StorageFailure(); }
                    }
                }
            }
            catch { Active?.StorageFailure(); }
            OnSaved(args.Status, args.Document, "save", false);
        }

        private static void OnDocumentSavedAs(object sender, DocumentSavedAsEventArgs args)
        { OnSaved(args.Status, args.Document, "save_as", true); }

        private static void OnSynchronized(object sender, DocumentSynchronizedWithCentralEventArgs args)
        {
            if (args.Document != null) SyncAuthorities.Remove(args.Document);
            OnSaved(args.Status, args.Document, "synchronize", false);
        }

        private static void OnSaved(RevitAPIEventStatus status, Document document, string action, bool newIdentity)
        {
            try
            {
                if (status == RevitAPIEventStatus.Succeeded && document != null)
                {
                    Documents.TryGetValue(document, out var prior);
                    Invalidate(prior);
                    var state = State(document, newIdentity);
                    if (Scoped())
                    {
                        Active.Attach(prior); Active.Attach(state);
                        Active.NativeCommitted = true;
                        Active.NativeOutcome = "committed";
                        Active.SaveOperation();
                    }
                }
                else if (Scoped()) ObserveCancelledOrFailed(status, action, document);
            }
            catch { if (Active != null) { Active.NativeOutcome = "unknown"; Active.Operation.Complete = false; } }
        }

        private static void ObserveCancelledOrFailed(RevitAPIEventStatus status, string action, Document document, FailureModel model = null)
        {
            if (status == RevitAPIEventStatus.Succeeded) return;
            if (Active.Cancelled && status == RevitAPIEventStatus.Cancelled) return; // Already captured by our pre-event.
            var state = document == null ? null : State(document);
            Active.Cancelled |= status == RevitAPIEventStatus.Cancelled;
            Active.NativeOutcome = status == RevitAPIEventStatus.Cancelled ? "cancelled" : "unknown";
            Active.Stop("REVIT_OPERATION_REQUIRES_USER", state);
            if (status != RevitAPIEventStatus.Cancelled) Active.Operation.Complete = false;
            var incident = Active.NewIncident(state, "native", action, action: Active.NativeOutcome);
            if (model != null)
            {
                incident.ModelScopeKey = model.ModelScopeKey; incident.ModelTitle = model.Title;
                Active.ClaimModel(model);
            }
            incident.Complete = status == RevitAPIEventStatus.Cancelled;
            incident.NativeAction = action;
            incident.NativeContextDescription = "Revit reported native action status " + status + ". Inspect the model before another attempt.";
            Active.SaveOperation(); Active.Record(incident);
        }

        private static bool Guard(RevitAPIPreEventArgs args, DocumentState state, FailureModel model, string action,
            string contextHash, string revision, string description, bool requiresGrant, bool complete)
        {
            if (!Scoped()) return true;
            if (model == null) throw new InvalidOperationException("Native model identity is unavailable.");
            Active.ClaimModel(model);
            var gate = Store.GetGate(model.ModelScopeKey, Active.Operation.OperationId);
            bool blocked = Active.Stopped || gate.Blocked || UncertainModels.Contains(model.ModelScopeKey);
            if (!blocked && complete && !requiresGrant && !args.IsCancelled()) return true;

            var incident = Active.NewIncident(state, "native", action);
            incident.ModelScopeKey = model.ModelScopeKey; incident.ModelTitle = model.Title;
            incident.NativeAction = action; incident.NativeContextHash = complete ? contextHash : null;
            incident.NativeContextDescription = FailureCapture.SafeText(description);
            incident.DocumentRevision = revision; incident.OperationContext = action; incident.Complete = complete;
            incident.Severity = "NativeOperation";
            var grant = !blocked && complete && !args.IsCancelled()
                ? FailurePolicy.MatchNativeGrant(Store.GetGrants(model.ModelScopeKey), model.ModelScopeKey, Year, action,
                    contextHash, Active.Operation.RequestHash, revision) : null;
            Active.SaveOperation();
            if (grant != null && !Active.StorageFailed)
            {
                incident.State = "accepted_once"; incident.Action = "native_allowed"; incident.GrantIds.Add(grant.GrantId);
                Active.Record(incident, incident.GrantIds);
                if (!Active.StorageFailed) return true;
            }
            else
            {
                if (action == "open" && complete)
                {
                    foreach (var key in NativeRequests.Where(pair => DateTimeOffset.UtcNow - pair.Value.CreatedUtc > TimeSpan.FromMinutes(5)).Select(pair => pair.Key).ToList()) NativeRequests.Remove(key);
                    if (NativeRequests.Count < 50) NativeRequests[contextHash] = incident;
                }
                Cancel(args, state, action, Active.StorageFailed ? "FAILURE_STORAGE_FAILED"
                    : Active.ErrorCode ?? (blocked ? "REVIT_MODEL_BLOCKED" : "REVIT_OPERATION_REQUIRES_USER"));
                incident.Action = Active.NativeOutcome == "cancelled" ? "cancelled" : "enforcement_unavailable";
                if (Active.NativeOutcome != "cancelled") incident.Complete = false;
                Active.Record(incident);
                return false;
            }
            Cancel(args, state, action, Active.StorageFailed ? "FAILURE_STORAGE_FAILED" : Active.ErrorCode ?? "REVIT_OPERATION_REQUIRES_USER");
            return false;
        }

        private static void DenyIncomplete(RevitAPIPreEventArgs args, DocumentState state, string action, string description, FailureModel model = null)
        {
            if (!Scoped()) return;
            try
            {
                model = model ?? state?.Model;
                if (model != null)
                {
                    Guard(args, state, model, action, null, state == null ? null : Revision(state), description, true, false);
                    return;
                }
            }
            catch { Active.StorageFailure(); }
            Active.Operation.Complete = false;
            Cancel(args, state, action, Active.StorageFailed ? "FAILURE_STORAGE_FAILED" : "FAILURE_CAPTURE_INCOMPLETE");
        }

        private static void Cancel(RevitAPIPreEventArgs args, DocumentState state, string action, string code)
        {
            Active.Stop(code, state);
            bool cancelled = false;
            try { if (args.Cancellable && !args.IsCancelled()) args.Cancel(); cancelled = args.IsCancelled(); }
            catch { }
            Active.Cancelled |= cancelled;
            Active.NativeOutcome = cancelled ? "cancelled" : "unknown";
            if (!cancelled) Active.Operation.Complete = false;
            if (action == "save" || action == "save_as" || action == "synchronize")
                _blockedSave = new SaveCorrelation { OperationId = Active.Operation.OperationId,
                    ModelScopeKey = state?.Model.ModelScopeKey ?? Active.Operation.ActiveModelBefore, Time = DateTimeOffset.UtcNow };
        }

        private static void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs args)
        {
            PendingRequestLifecycle.SetHostBusy(true);
            var application = sender as UIApplication;
            RevitMessageCatalog.EnsureLoaded(application);
            var task = args.GetType() == typeof(TaskDialogShowingEventArgs) ? (TaskDialogShowingEventArgs)args : null;
            var box = args as MessageBoxShowingEventArgs;
            var kind = task != null ? "TaskDialog" : box != null ? "MessageBox" : "Dialog";
            var text = task?.Message ?? box?.Message;
            if (!Scoped())
            {
                // Outside MCP the user handles the dialog exactly as Revit shows it; it is only noted for the next MCP call.
                if (args.DialogId == "TaskDialog_Project_Not_Saved_Recently" && _blockedSave != null
                    && DateTimeOffset.UtcNow - _blockedSave.Time < TimeSpan.FromMinutes(2))
                    PendingRequestLifecycle.SetHostBusy(true, new JObject { ["correlation"] = "correlated_not_proven",
                        ["operationId"] = _blockedSave.OperationId, ["modelScopeKey"] = _blockedSave.ModelScopeKey });
                RecordUserDialog(application, args.DialogId, kind, text);
                return;
            }

            try
            {
                var dialog = RevitMessageLookup.DialogInfo(args.DialogId, kind, text, RevitMessageCatalog.FindDialog(args.DialogId));
                if (OwnDialogPending && string.IsNullOrEmpty(args.DialogId))
                {
                    // revit_show_message waits for its own dialog: report the wait, but it is not a Revit condition.
                    dialog["kind"] = "agent_message";
                    Active.Operation.Execution = "waiting_for_user";
                    Active.SaveOperation();
                    Active.Lifecycle.EmitDialogWait(new JObject { ["dialogId"] = "", ["manual"] = true, ["dialog"] = dialog,
                        ["message"] = FailureCapture.SafeText(text ?? "Revit is waiting for a user action.", 1024) });
                    return;
                }
                string context = null;
                if (Opening.Count > 0 || _lastOpened != null)
                {
                    if (args.DialogId == "TaskDialog_Unresolved_References") context = "open:ignore_unresolved_references";
                    if (args.DialogId == "TaskDialog_Views_Related_To_Analytical_Changed") context = "open:acknowledge_analytical_changes";
                }
                var attempt = Opening.Count > 0 ? Opening[Opening.Count - 1] : _lastOpened;
                var state = attempt == null ? State(Active.Application.ActiveUIDocument?.Document) : null;
                var model = attempt?.Model ?? state?.Model;
                var adapter = task == null ? null : FailureDialogAdapters.Get(args.DialogId, Year, context);
                if (model != null)
                {
                    Active.ClaimModel(model);
                    var incident = Active.NewIncident(state, "dialog", args.DialogId, "observed", "waiting_for_user");
                    incident.ModelScopeKey = model.ModelScopeKey; incident.ModelTitle = model.Title;
                    incident.DocumentRevision = attempt?.Revision ?? Revision(state);
                    incident.OperationContext = context ?? "manual_dialog";
                    incident.Severity = "TaskDialog"; incident.MessageCount = 1;
                    incident.Messages.Add(new FailureMessage { DefinitionId = args.DialogId,
                        Severity = task == null ? "Dialog" : "TaskDialog", Source = "dialog",
                        Description = FailureCapture.SafeText(text ?? "Native Revit dialog requires user interaction.") });
                    var policy = adapter == null || Active.Stopped ? null : FailurePolicy.EvaluateDialog(args.DialogId, Year,
                        context, model.ModelScopeKey, Active.Operation.RequestHash, incident.DocumentRevision,
                        Store.GetRules(model.ModelScopeKey), Store.GetGrants(model.ModelScopeKey));
                    if (policy?.Action == "continue")
                    {
                        incident.State = policy.GrantIds.Count == 0 ? "accepted_by_rule" : "accepted_once";
                        incident.Action = "dialog_answer_authorized"; incident.RuleIds = policy.RuleIds; incident.GrantIds = policy.GrantIds;
                    }
                    Active.SaveOperation(); Active.Record(incident, policy?.GrantIds);
                    // Exact type/id/year/context semantics only. DocWarnDialog never has an adapter.
                    if (policy?.Action == "continue" && !Active.StorageFailed && adapter != null)
                    {
                        bool answered = task.OverrideResult(adapter.Result);
                        var receipt = Active.NewIncident(state, "dialog_result", args.DialogId, "observed",
                            answered ? "dialog_answered" : "dialog_answer_not_applied");
                        receipt.ModelScopeKey = model.ModelScopeKey; receipt.ModelTitle = model.Title;
                        receipt.DocumentRevision = incident.DocumentRevision; receipt.OperationContext = context;
                        receipt.RuleIds = policy.RuleIds; receipt.GrantIds = policy.GrantIds;
                        receipt.NativeContextDescription = "Exact adapter result " + adapter.Result + ": " + adapter.ButtonSemantic
                            + "; native OverrideResult accepted=" + answered + ".";
                        Active.Record(receipt); // The grant was consumed by the authorization record, once only.
                        if (answered)
                        {
                            if (!Active.DialogAnswers.TryGetValue(args.DialogId ?? "", out var answers))
                                Active.DialogAnswers[args.DialogId ?? ""] = answers = new List<JObject>();
                            answers.Add(new JObject { ["button"] = adapter.Result.ToString(), ["meaning"] = adapter.ButtonSemantic });
                            PendingRequestLifecycle.SetHostBusy(false);
                            return;
                        }
                    }
                }
                Active.Operation.Execution = "waiting_for_user";
                Active.SaveOperation();
                Active.Lifecycle.EmitDialogWait(new JObject { ["dialogId"] = FailureCapture.SafeText(args.DialogId, 128),
                    ["message"] = FailureCapture.SafeText(text ?? "Revit is waiting for a user action.", 1024),
                    ["manual"] = true, ["dialog"] = dialog });
            }
            catch
            {
                Active.StorageFailure(); Active.Operation.Execution = "waiting_for_user";
                Active.Lifecycle.EmitDialogWait(new JObject { ["manual"] = true, ["message"] = "Revit is waiting for a user action; recording the dialog failed." });
            }
        }

        // Attribution: during a manual open, the model being opened; otherwise the active model.
        private static void RecordUserDialog(UIApplication application, string code, string kind, string text)
        {
            try
            {
                string path, title;
                var opening = ManualOpening.Count > 0 ? ManualOpening[ManualOpening.Count - 1] : null;
                if (!string.IsNullOrEmpty(opening)) { path = opening; title = Path.GetFileNameWithoutExtension(opening); }
                else { var document = application?.ActiveUIDocument?.Document; path = document?.PathName; title = document?.Title; }
                UserDialogs.Add(UserDialogBuffer.Key(path, title), title,
                    RevitMessageLookup.DialogInfo(code, kind, text, RevitMessageCatalog.FindDialog(code), UserDialogBuffer.MaxText), DateTimeOffset.UtcNow);
            }
            catch { }
        }

        // Read only: no processing result is set and no warning is removed, so Revit shows the user what it always shows.
        private static void RecordUserFailures(FailuresProcessingEventArgs args)
        {
            try
            {
                var accessor = args.GetFailuresAccessor();
                var failures = accessor.GetFailureMessages();
                if (failures.Count == 0) return;
                var document = accessor.GetDocument();
                var key = UserDialogBuffer.Key(document?.PathName, document?.Title);
                if (key == null) return;
                UserDialogs.AddFailures(key, document.Title, failures,
                    failure => failure.GetFailureDefinitionId().Guid.ToString(), (failure, code) =>
                {
                    var item = new JObject { ["kind"] = "failure", ["code"] = code };
                    try { item["severity"] = failure.GetSeverity().ToString(); } catch { }
                    try { item["text"] = failure.GetDescriptionText(); } catch { }
                    var name = RevitMessageCatalog.FindFailure(item.Value<string>("code"))?.Name;
                    if (name != null) item["name"] = name;
                    return item;
                }, DateTimeOffset.UtcNow);
            }
            catch { }
        }

        private static void ManualOpened(DocumentOpenedEventArgs args)
        {
            if (ManualOpening.Count == 0) return;
            var path = ManualOpening[ManualOpening.Count - 1];
            ManualOpening.RemoveAt(ManualOpening.Count - 1);
            try
            {
                // A new local copy of a central model opens under a path other than the one requested.
                var document = args.Document;
                if (args.Status == RevitAPIEventStatus.Succeeded && document != null)
                    UserDialogs.Rekey(UserDialogBuffer.Key(path, null), UserDialogBuffer.Key(document.PathName, document.Title), document.Title);
            }
            catch { }
        }
    }
}
