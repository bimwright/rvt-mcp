using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RvtMcp.Plugin.Handlers
{
    internal static class SavedViewOrientationRefresh
    {
        private static readonly Dictionary<Document, Dictionary<ElementId, ViewOrientation3D>> Tracked =
            new Dictionary<Document, Dictionary<ElementId, ViewOrientation3D>>();
        private static readonly Dictionary<Document, HashSet<ElementId>> Pending =
            new Dictionary<Document, HashSet<ElementId>>();
        private static bool _subscribed;

        internal static void Track(UIApplication app, View3D view)
        {
            if (!_subscribed)
            {
                app.Application.DocumentChanged += OnDocumentChanged;
                app.Application.DocumentClosed += OnDocumentClosed;
                app.Idling += OnIdling;
                _subscribed = true;
            }
            if (!Tracked.TryGetValue(view.Document, out var views))
                Tracked.Add(view.Document, views = new Dictionary<ElementId, ViewOrientation3D>());
            views[view.Id] = view.GetSavedOrientation();
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs args)
        {
            var doc = args.GetDocument();
            if (!Tracked.TryGetValue(doc, out var views)) return;
            var replay = args.Operation == UndoOperation.TransactionUndone
                || args.Operation == UndoOperation.TransactionRedone
                || args.Operation == UndoOperation.TransactionGroupRolledBack;
            IEnumerable<ElementId> changed = args.Operation == UndoOperation.TransactionGroupRolledBack
                ? views.Keys.ToArray()
                : args.GetModifiedElementIds().Concat(args.GetAddedElementIds());
            foreach (var id in changed)
            {
                if (!views.TryGetValue(id, out var previous) || !(doc.GetElement(id) is View3D view)) continue;
                var saved = view.GetSavedOrientation();
                views[id] = saved;
                if (!replay || Same(previous, saved)) continue;
                if (!Pending.TryGetValue(doc, out var pending))
                    Pending.Add(doc, pending = new HashSet<ElementId>());
                pending.Add(id);
            }
        }

        private static void OnIdling(object sender, IdlingEventArgs args)
        {
            // Undo restores saved camera data but may leave Revit's temporary orientation cached.
            // Refresh only that UI state after the read-only DocumentChanged callback has returned.
            var pending = Pending.ToArray();
            Pending.Clear();
            foreach (var document in pending)
            {
                if (!document.Key.IsValidObject) continue;
                foreach (var id in document.Value)
                {
                    var view = document.Key.GetElement(id) as View3D;
                    if (view == null || view.IsLocked) continue;
                    try { view.SetOrientation(view.GetSavedOrientation()); }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                    {
                        App.DebugLog("Saved view orientation refresh failed: " + ex.Message);
                    }
                }
            }
        }

        private static void OnDocumentClosed(object sender, DocumentClosedEventArgs args)
        {
            PruneClosedDocuments(Tracked);
            PruneClosedDocuments(Pending);
        }

        private static void PruneClosedDocuments<T>(Dictionary<Document, T> documents)
        {
            // DocumentClosed also fires after cancellation/failure. Rebuild without hashing
            // invalid native wrappers, while preserving documents that are still open.
            var live = documents.Where(pair => pair.Key.IsValidObject).ToArray();
            if (live.Length == documents.Count) return;
            documents.Clear();
            foreach (var pair in live) documents.Add(pair.Key, pair.Value);
        }

        private static bool Same(ViewOrientation3D left, ViewOrientation3D right)
            => left.EyePosition.IsAlmostEqualTo(right.EyePosition)
                && left.ForwardDirection.IsAlmostEqualTo(right.ForwardDirection)
                && left.UpDirection.IsAlmostEqualTo(right.UpDirection);
    }
}
