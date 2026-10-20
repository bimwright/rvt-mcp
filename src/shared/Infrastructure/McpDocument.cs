using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin
{
    /// <summary>Explicit discard for documents opened by the current synchronous send-code invocation.</summary>
    public static class McpDocument
    {
        [ThreadStatic] private static ScriptScope _scope;
        internal static IDisposable Begin(UIApplication app) => new ScriptScope(app);

        public static Document TrackOpened(Document document)
        {
            if (_scope != null && document != null && document.IsValidObject
                && !_scope.Initial.Any(item => Same(item, document))
                && !_scope.Owned.Any(item => Same(item, document)))
                _scope.Owned.Add(document);
            return document;
        }

        public static bool Close(Document document, bool saveModified)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            // Save-on-close and documents already open before the script retain native policy.
            if (saveModified || _scope == null || !_scope.Owned.Any(item => Same(item, document)))
                return document.Close(saveModified);
            var scope = _scope;
            var previous = scope.Discarding;
            scope.Discarding = document;
            try { return document.Close(false); }
            finally { scope.Discarding = previous; }
        }

        internal static bool IsDiscardAuthorized(Document document) => _scope?.Discarding != null && Same(_scope.Discarding, document);

        private static bool Same(Document first, Document second)
        {
            if (ReferenceEquals(first, second)) return true;
            return first != null && second != null && first.IsValidObject && second.IsValidObject && first.Equals(second);
        }

        private sealed class ScriptScope : IDisposable
        {
            internal readonly List<Document> Initial;
            internal readonly List<Document> Owned = new List<Document>();
            internal Document Discarding;
            internal ScriptScope(UIApplication app)
            {
                if (_scope != null) throw new InvalidOperationException("A script document scope is already active.");
                Initial = app.Application.Documents.Cast<Document>().Where(doc => doc.IsValidObject).ToList();
                _scope = this;
            }
            public void Dispose()
            {
                Discarding = null;
                if (ReferenceEquals(_scope, this)) _scope = null;
            }
        }
    }
}
