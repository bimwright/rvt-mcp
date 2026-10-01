using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>One UI-thread subscription per shell. Events outside a synchronous MCP scope are ignored.</summary>
    public static class McpChangeTracker
    {
        private static Scope _active;

        public static Scope Begin()
        {
            if (_active != null) throw new InvalidOperationException("A change capture scope is already active.");
            return _active = new Scope();
        }

        public static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            var scope = _active;
            if (scope == null) return;
            try { scope.Record(e); }
            catch { scope.Incomplete = true; } // Diagnostics must never interrupt a model transaction.
        }

        public sealed class Scope : IDisposable
        {
            private readonly Dictionary<Document, DocumentChangeAccumulator> _documents = new Dictionary<Document, DocumentChangeAccumulator>();
            private readonly Dictionary<Document, string> _titles = new Dictionary<Document, string>();
            internal bool Incomplete;

            internal void Record(DocumentChangedEventArgs e)
            {
                if (e.Operation == UndoOperation.TransactionRolledBack) return;
                var doc = e.GetDocument();
                if (!_documents.TryGetValue(doc, out var changes))
                {
                    if (_documents.Count >= 8) { Incomplete = true; return; }
                    _documents[doc] = changes = new DocumentChangeAccumulator();
                    // B has no persistence consumer: do not collect local/cloud model paths at all.
                    _titles[doc] = doc.Title;
                }
                if (e.Operation != UndoOperation.TransactionCommitted)
                {
                    changes.MarkIncomplete("A group rollback or undo occurred; final element changes cannot be reconstructed from this event.");
                    return;
                }
                foreach (var name in e.GetTransactionNames()) changes.AddTransaction(name);
                Observe(doc, changes, e.GetAddedElementIds(), "added");
                Observe(doc, changes, e.GetModifiedElementIds(), "modified");
                Observe(doc, changes, e.GetDeletedElementIds(), "deleted");
            }

            private static void Observe(Document doc, DocumentChangeAccumulator changes, ICollection<ElementId> ids, string kind)
            {
                foreach (var id in ids)
                {
                    // Deleted elements no longer exist. Do not scan the whole model to guess their category.
                    var element = kind == "deleted" ? null : doc.GetElement(id);
                    changes.Observe(RevitCompat.GetId(id), kind, element?.Category?.Name);
                }
            }

            public JObject Snapshot(bool batchRolledBack = false)
            {
                // batch_execute owns a single outer group and guarantees all its commands were rolled back.
                if (batchRolledBack) return null;
                var documents = new JArray();
                foreach (var pair in _documents)
                {
                    var item = pair.Value.Snapshot(_titles[pair.Key]);
                    if (item?.Value<string>("status") == "incomplete") Incomplete = true;
                    if (item != null) documents.Add(item);
                }
                if (documents.Count == 0 && !Incomplete) return null;
                return new JObject
                {
                    ["documents"] = documents,
                    ["complete"] = !Incomplete,
                    ["note"] = Incomplete ? "Capture was incomplete; do not infer that unlisted elements were unchanged." : null
                };
            }

            public void Dispose()
            {
                if (ReferenceEquals(_active, this)) _active = null;
            }
        }
    }
}
