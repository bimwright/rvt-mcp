using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Changes
{
    public sealed class ReportChangeBuffer
    {
        private const int Limit = 5000;
        private readonly object _lock = new object();
        private State _pending;
        private State _retained;
        private string _taskId;
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
            public readonly Dictionary<long, Element> Elements = new Dictionary<long, Element>();
        }

        private sealed class State
        {
            public bool Complete = true;
            public bool Truncated;
            public readonly List<DocumentState> Documents = new List<DocumentState>();
        }

        public void Push(CommandChanges command)
        {
            if (command == null) return;
            lock (_lock)
            {
                if (_pending == null) _pending = new State();
                if (command.Incomplete) _pending.Complete = false;
                foreach (var source in command.Documents)
                {
                    var document = GetDocument(_pending, source.Document);
                    if (source.Incomplete) { _pending.Complete = false; continue; }
                    MergeElements(_pending, document, source.Added, "added");
                    MergeElements(_pending, document, source.Modified, "modified");
                    MergeElements(_pending, document, source.Deleted, "deleted");
                }
            }
        }

        public void MarkIncomplete(string token)
        {
            if (token == null) return;
            lock (_lock)
            {
                Mark(_pending, token);
                Mark(_retained, token);
            }
        }

        public ReportChangeSet TakeForReport(string taskId)
        {
            lock (_lock)
            {
                if (_retained != null && string.Equals(_taskId, taskId, StringComparison.Ordinal))
                {
                    if (_pending != null) MergeState(_retained, _pending);
                }
                else
                {
                    _retained = _pending;
                    _taskId = taskId;
                }
                _pending = null;
                return _retained == null ? null : Snapshot(_retained);
            }
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
            var result = new ReportChangeSet { Changes = new ReportChanges { Complete = state.Complete, Truncated = state.Truncated } };
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
    }
}
