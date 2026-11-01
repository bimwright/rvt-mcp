using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Changes
{
    public sealed class ReportChangeBuffer
    {
        private const int Limit = 5000;
        private readonly object _lock = new object();
        private State _pending;
        private readonly LinkedList<PendingCapture> _captures = new LinkedList<PendingCapture>();
        private readonly Dictionary<string, LinkedListNode<PendingCapture>> _captureIds = new Dictionary<string, LinkedListNode<PendingCapture>>(StringComparer.Ordinal);
        private readonly HashSet<string> _lostSessions = new HashSet<string>(StringComparer.Ordinal);
        private long _sequence;
        private int _captureElements;
        public const int PendingCallLimit = 512;
        public const int PendingElementLimit = 50000;
        private readonly Dictionary<string, State> _retained = new Dictionary<string, State>(StringComparer.Ordinal);
        public static ReportChangeBuffer Shared { get; } = new ReportChangeBuffer();

        private sealed class Element
        {
            public string Kind;
            public string UniqueId;
            public string Category;
        }

        private sealed class DocumentState
        {
            public ReportDocument Document;
            public Dictionary<long, Element> Elements = new Dictionary<long, Element>();
        }

        private sealed class State
        {
            public bool Saved;
            public bool HasCapture;
            public string Scope = "process";
            public long LastSequence;
            public readonly List<ReportCallReference> Calls = new List<ReportCallReference>();
            public HashSet<string> SavedCallIds;
            public int SavedReferenceCount;
            public bool Complete = true;
            public bool Truncated;
            public readonly List<DocumentState> Documents = new List<DocumentState>();
        }

        private sealed class PendingCapture
        {
            public string Id, Session;
            public long Sequence;
            public int Elements;
            public State State;
        }

        public void Push(CommandChanges command)
        {
            if (command == null) return;
            lock (_lock)
            {
                if (_pending == null) _pending = new State();
                Accumulate(_pending, command);
            }
        }

        private static void Accumulate(State state, CommandChanges command)
        {
            state.HasCapture = true;
            if (command.Incomplete) state.Complete = false;
            foreach (var source in command.Documents)
            {
                var document = GetDocument(state, source.Document);
                if (source.Incomplete) { state.Complete = false; continue; }
                MergeElements(state, document, source.Added, "added");
                MergeElements(state, document, source.Modified, "modified");
                MergeElements(state, document, source.Deleted, "deleted");
            }
        }

        public bool Push(CommandChanges command, ReportCallContext context, string tool)
        {
            if (context == null) { Push(command); return true; }
            if (!context.IsValid) throw new ReportCaptureException("REPORT_CONTEXT_INVALID: Invalid capture metadata.");
            if (command == null) return false;
            lock (_lock)
            {
                if (_captureIds.ContainsKey(context.CallId)) throw new ReportCaptureException("REPORT_CAPTURE_DUPLICATE: Capture ID already exists.");
                var state = new State { Scope = "client", LastSequence = ++_sequence };
                Accumulate(state, command);
                var keys = command.Documents.Select(d => d.Document.ModelKey).Distinct().ToList();
                if (keys.Count == 0) keys.Add(null);
                foreach (var key in keys) state.Calls.Add(new ReportCallReference { CallId = context.CallId, Session = context.Session,
                    Tool = tool, At = ReportJson.FormatUtc(DateTime.UtcNow), ModelKey = key, HistoryRequested = context.HistoryRequested });
                var entry = new PendingCapture { Id = context.CallId, Session = context.Session, Sequence = state.LastSequence,
                    State = state, Elements = state.Documents.Sum(d => d.Elements.Count) };
                _captureIds.Add(entry.Id, _captures.AddLast(entry));
                _captureElements += entry.Elements;
                while (_captures.Count > PendingCallLimit || _captureElements > PendingElementLimit)
                {
                    _lostSessions.Add(_captures.First.Value.Session);
                    RemoveCapture(_captures.First);
                }
                return _captureIds.ContainsKey(entry.Id);
            }
        }

        private void RemoveCapture(LinkedListNode<PendingCapture> node)
        {
            _captureIds.Remove(node.Value.Id);
            _captureElements -= node.Value.Elements;
            _captures.Remove(node);
        }

        public void MarkCaptureUnavailable(string session)
        {
            lock (_lock)
            {
                if (session != null) _lostSessions.Add(session);
                else
                {
                    _pending = _pending ?? new State();
                    _pending.HasCapture = true; _pending.Complete = false;
                }
            }
        }

        public void MarkIncomplete(string token)
        {
            if (token == null) return;
            lock (_lock)
            {
                Mark(_pending, token);
                foreach (var capture in _captures) Mark(capture.State, token);
                foreach (var state in _retained.Values) Mark(state, token);
            }
        }

        public ReportChangeSet TakeForReport(string taskId, Func<AgentReport> loadPrevious = null, ReportSubmissionScope scope = null)
        {
            scope = scope ?? new ReportSubmissionScope();
            scope.Validate();
            lock (_lock)
            {
                var key = ReportSource.TaskKey(taskId, scope.Session);
                _retained.TryGetValue(key, out var state);
                try
                {
                    if (state == null || state.Saved)
                    {
                        var restored = Restore(loadPrevious?.Invoke());
                        if (state != null)
                        {
                            if (restored == null) restored = new State();
                            MergeState(restored, state);
                        }
                        state = restored;
                    }
                }
                catch
                {
                    // Without retained ownership metadata, a failed read cannot validate a new claim.
                    if (state == null && scope.Session != null) throw;
                    state = state ?? new State { Saved = true, Scope = scope.Kind };
                    _retained[key] = scope.Session == null ? MergePending(state) : ClaimScoped(state, scope);
                    throw;
                }
                state = scope.Session == null ? MergePending(state) : ClaimScoped(state, scope);
                if (state == null) return null;
                _retained[key] = state;
                return state.HasCapture ? Snapshot(state) : null;
            }
        }

        private State ClaimScoped(State state, ReportSubmissionScope scope)
        {
            if (state != null && scope.CallIds != null && state.Scope != "calls")
                throw new ReportCaptureException("REPORT_SCOPE_CONFLICT: This task used an interval scope; resend it without call_ids.");
            var mode = state?.Scope ?? scope.Kind;
            var known = new HashSet<string>(state?.Calls.Select(c => c.CallId) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (state?.SavedCallIds != null) known.UnionWith(state.SavedCallIds);
            var ids = scope.CallIds;
            if (ids != null)
                foreach (var id in ids)
                {
                    if (known.Contains(id)) continue;
                    if (!_captureIds.TryGetValue(id, out var node) || node.Value.Session != scope.Session)
                        throw new ReportCaptureException("REPORT_CAPTURE_UNAVAILABLE: A capture is missing, expired, assigned, or belongs to another client: " + id);
                }
            var selected = _captures.Where(c => c.Session == scope.Session && (ids != null ? ids.Contains(c.Id) : mode != "calls"))
                .OrderBy(c => c.Sequence).ToList();
            if (selected.Any(c => c.Sequence <= (state?.LastSequence ?? 0)))
                throw new ReportCaptureException("REPORT_CAPTURE_ORDER: Older unclaimed calls cannot be appended after newer captures. Report the older calls separately.");
            if ((state?.SavedReferenceCount ?? 0) + (state?.Calls.Count ?? 0) + selected.Sum(c => c.State.Calls.Count) > 1024)
                throw new ReportCaptureException("REPORT_CAPTURE_LIMIT: A report can retain at most 1024 call/model references; start a separate report.");
            state = state ?? new State { Scope = mode };
            foreach (var capture in selected)
            {
                MergeState(state, capture.State);
                RemoveCapture(_captureIds[capture.Id]);
            }
            if (ids == null && mode == "client" && _lostSessions.Remove(scope.Session))
            {
                state.HasCapture = true;
                state.Complete = false;
                state.Truncated = true;
            }
            return state;
        }

        private State MergePending(State state)
        {
            if (_pending == null) return state;
            if (state == null) state = _pending;
            else MergeState(state, _pending);
            _pending = null;
            return state;
        }

        public void ConfirmSaved(string taskId, string session = null)
        {
            lock (_lock)
            {
                if (!_retained.TryGetValue(ReportSource.TaskKey(taskId, session), out var state)) return;
                // JSON holds element lists; retain only invalidation and atomic retry metadata.
                foreach (var document in state.Documents) document.Elements = new Dictionary<long, Element>();
                state.Saved = true;
                state.SavedCallIds = new HashSet<string>(state.Calls.Select(c => c.CallId), StringComparer.Ordinal);
                state.SavedReferenceCount = state.Calls.Count;
                state.Calls.Clear();
            }
        }

        internal string ScopeFor(string taskId, string session, string fallback)
        {
            lock (_lock) return _retained.TryGetValue(ReportSource.TaskKey(taskId, session), out var state) ? state.Scope : fallback;
        }

        private static State Restore(AgentReport report)
        {
            if (report?.Changes == null) return report?.Source?.CaptureScope == null ? null : new State { Scope = report.Source.CaptureScope };
            var state = new State { Complete = report.Changes.Complete, Truncated = report.Changes.Truncated,
                HasCapture = true, Scope = report.Changes.Scope, LastSequence = report.Changes.CaptureSequence ?? 0 };
            if (report.Changes.Calls != null) state.Calls.AddRange(report.Changes.Calls.Select(CopyCall));
            foreach (var changes in report.Changes.Documents ?? new List<ReportDocumentChanges>())
            {
                var metadata = report.Documents?.FirstOrDefault(d => d != null && d.Ref == changes?.Doc);
                if (metadata?.Token == null) throw new IOException("Cannot restore report changes: missing document identity.");
                var document = GetDocument(state, metadata);
                MergeElements(state, document, changes.Added ?? new List<ReportChangedElement>(), "added");
                MergeElements(state, document, changes.Modified ?? new List<ReportChangedElement>(), "modified");
                MergeElements(state, document, changes.Deleted ?? new List<ReportChangedElement>(), "deleted");
            }
            return state;
        }

        private static void Mark(State state, string token)
        {
            if (state != null && state.Documents.Any(d => d.Document.Token == token)) state.Complete = false;
        }

        private static ReportDocument Copy(ReportDocument document)
        {
            return new ReportDocument { Token = document.Token, ModelKey = document.ModelKey, Title = document.Title,
                IsFamily = document.IsFamily, Kind = document.Kind, Ref = null };
        }

        private static DocumentState GetDocument(State state, ReportDocument metadata)
        {
            var document = state.Documents.FirstOrDefault(d => d.Document.Token == metadata.Token);
            if (document == null)
            {
                document = new DocumentState();
                state.Documents.Add(document);
            }
            document.Document = Copy(metadata);
            return document;
        }

        private static void MergeElements(State state, DocumentState document, IEnumerable<ReportChangedElement> elements, string kind)
        {
            foreach (var source in elements)
            {
                if (!document.Elements.TryGetValue(source.Id, out var existing))
                {
                    if (document.Elements.Count >= Limit) { state.Truncated = true; continue; }
                    existing = new Element { Kind = kind };
                    document.Elements.Add(source.Id, existing);
                }
                else if (existing.Kind == "added" && kind == "deleted")
                {
                    document.Elements.Remove(source.Id);
                    continue;
                }
                else if (existing.Kind == "added" || (existing.Kind == "modified" && kind == "added")) { }
                else if (existing.Kind == "deleted" && kind == "added") existing.Kind = "modified";
                else existing.Kind = kind;
                if (source.UniqueId != null) existing.UniqueId = source.UniqueId;
                if (source.Category != null) existing.Category = source.Category;
            }
        }

        private static void MergeState(State target, State source)
        {
            target.HasCapture |= source.HasCapture;
            target.LastSequence = Math.Max(target.LastSequence, source.LastSequence);
            foreach (var call in source.Calls)
                if (!target.Calls.Any(c => c.CallId == call.CallId && c.ModelKey == call.ModelKey)) target.Calls.Add(CopyCall(call));
            target.Complete &= source.Complete;
            target.Truncated |= source.Truncated;
            foreach (var item in source.Documents)
            {
                var document = GetDocument(target, item.Document);
                foreach (var pair in item.Elements)
                    MergeElements(target, document, new[] { new ReportChangedElement { Id = pair.Key,
                        UniqueId = pair.Value.UniqueId, Category = pair.Value.Category } }, pair.Value.Kind);
            }
        }

        private static ReportChangeSet Snapshot(State state)
        {
            var result = new ReportChangeSet { Changes = new ReportChanges { Complete = state.Complete, Truncated = state.Truncated, Scope = state.Scope,
                Calls = state.Calls.Count == 0 ? null : state.Calls.Select(CopyCall).ToList(), CaptureSequence = state.LastSequence == 0 ? (long?)null : state.LastSequence } };
            foreach (var item in state.Documents)
            {
                if (item.Elements.Count == 0) continue;
                result.Documents.Add(Copy(item.Document));
                var changes = new ReportDocumentChanges { Doc = item.Document.Token };
                foreach (var pair in item.Elements.OrderBy(x => x.Key))
                {
                    var element = new ReportChangedElement { Id = pair.Key, UniqueId = pair.Value.UniqueId, Category = pair.Value.Category };
                    if (pair.Value.Kind == "added") changes.Added.Add(element);
                    else if (pair.Value.Kind == "modified") changes.Modified.Add(element);
                    else changes.Deleted.Add(element);
                }
                result.Changes.Documents.Add(changes);
            }
            return result;
        }

        private static ReportCallReference CopyCall(ReportCallReference call) => new ReportCallReference { CallId = call.CallId,
            Session = call.Session, Tool = call.Tool, At = call.At, ModelKey = call.ModelKey, HistoryRequested = call.HistoryRequested };
    }
}
