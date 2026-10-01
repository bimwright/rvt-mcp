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

        public static Scope Begin(bool history = false)
        {
            if (_active != null) throw new InvalidOperationException("A change capture scope is already active.");
            return _active = new Scope(history);
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
            private sealed class CapturedDocument
            {
                public readonly DocumentChangeAccumulator Changes = new DocumentChangeAccumulator();
                public string Title;
                public JObject Identity;
            }
            private readonly Dictionary<Document, CapturedDocument> _documents = new Dictionary<Document, CapturedDocument>();
            private readonly bool _history;
            internal bool Incomplete;
            internal Scope(bool history) { _history = history; }

            internal void Record(DocumentChangedEventArgs e)
            {
                if (e.Operation == UndoOperation.TransactionRolledBack) return;
                var doc = e.GetDocument();
                if (!_documents.TryGetValue(doc, out var captured))
                {
                    if (_documents.Count >= 8) { Incomplete = true; return; }
                    _documents[doc] = captured = new CapturedDocument
                    { Title = doc.Title, Identity = _history ? Identity(doc) : null };
                }
                var changes = captured.Changes;
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

            private void Observe(Document doc, DocumentChangeAccumulator changes, ICollection<ElementId> ids, string kind)
            {
                foreach (var id in ids)
                {
                    // Deleted elements no longer exist. Do not scan the whole model to guess their category.
                    var element = kind == "deleted" ? null : doc.GetElement(id);
                    changes.Observe(RevitCompat.GetId(id), kind, element?.Category?.Name, _history ? element?.UniqueId : null);
                }
            }

            internal static JObject Identity(Document doc)
            {
                if (doc == null) return null;
                try
                {
                    // A detached session must not write history into the source central's database.
                    if (doc.IsDetached) return null;
                    JObject identity;
                    if (doc.IsModelInCloud)
                    {
                        var cloud = doc.GetCloudModelPath();
                        identity = ChangeHistoryIdentity.Create(cloud.GetProjectGUID() + "/" + cloud.GetModelGUID(), "cloud", doc.Title);
                    }
                    else
                    {
                        var path = doc.IsWorkshared
                            ? ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()) : doc.PathName;
                        identity = ChangeHistoryIdentity.Create(path, doc.IsWorkshared ? "central" : "file", doc.Title, path);
                    }
                    if (identity != null)
                    {
                        identity["workshared"] = doc.IsWorkshared;
                        if (!doc.IsFamilyDocument)
                        {
                            identity["projectName"] = BakeRedactor.RedactForBake(doc.ProjectInformation?.Name ?? "");
                            identity["projectNumber"] = BakeRedactor.RedactForBake(doc.ProjectInformation?.Number ?? "");
                        }
                    }
                    return identity;
                }
                catch (Exception ex)
                {
                    HistoryDiagnostics.Report("history_identity", ex, log: App.DebugLog);
                    return null;
                } // Never assign a title-based identity when the authoritative identity is unavailable.
            }

            public JObject HistorySnapshot(Document activeDocument, bool batchRolledBack = false)
            {
                if (!_history) return null;
                var documents = new JArray();
                if (!batchRolledBack)
                    // Do not hash/access a Document here: arbitrary code can close it
                    // before returning. All metadata was captured while it was valid.
                    foreach (var captured in _documents.Values)
                    {
                        var summary = captured.Changes.Snapshot(captured.Title);
                        if (summary != null) documents.Add(new JObject
                        {
                            ["model"] = captured.Identity, ["summary"] = summary,
                            ["elements"] = captured.Changes.HistoryElements()
                        });
                    }
                if (documents.Count == 0) return null;
                return new JObject { ["activeModel"] = Identity(activeDocument), ["documents"] = documents,
                    ["complete"] = batchRolledBack || !Incomplete };
            }

            public JObject Snapshot(bool batchRolledBack = false)
            {
                // batch_execute owns a single outer group and guarantees all its commands were rolled back.
                if (batchRolledBack) return null;
                var documents = new JArray();
                foreach (var captured in _documents.Values)
                {
                    var item = captured.Changes.Snapshot(captured.Title);
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
