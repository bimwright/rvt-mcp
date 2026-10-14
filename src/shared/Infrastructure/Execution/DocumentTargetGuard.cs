using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    // Only accessed on the Revit UI thread. Tokens identify this open document lifetime,
    // including unsaved projects and family documents; names and file paths are not identities.
    internal static class DocumentTargetGuard
    {
        private static readonly Dictionary<Document, string> Documents = new Dictionary<Document, string>();

        internal static JObject Capture(UIApplication app)
        {
            // Removing an invalid Revit Document hashes its dead native wrapper.
            // Rebuild from live keys so closed documents are never hashed again.
            var live = Documents.Where(pair => pair.Key.IsValidObject).ToArray();
            if (live.Length != Documents.Count)
            {
                Documents.Clear();
                foreach (var pair in live) Documents.Add(pair.Key, pair.Value);
            }
            var doc = app.ActiveUIDocument?.Document;
            string documentId = null;
            if (doc != null && doc.IsValidObject && !Documents.TryGetValue(doc, out documentId))
                Documents.Add(doc, documentId = Guid.NewGuid().ToString("N"));
            using (var process = Process.GetCurrentProcess())
                return new JObject
                {
                    ["target_id"] = "revit-" + app.Application.VersionNumber + "-" + process.Id,
                    ["pid"] = process.Id,
                    ["host_year"] = int.Parse(app.Application.VersionNumber),
                    ["process_start_utc"] = process.StartTime.ToUniversalTime().ToString("o"),
                    ["window_title"] = process.MainWindowTitle,
                    ["document_id"] = documentId,
                    ["document_title"] = doc?.Title,
                    ["is_family_document"] = doc == null ? JValue.CreateNull() : new JValue(doc.IsFamilyDocument)
                };
        }
    }
}
