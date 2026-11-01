using Autodesk.Revit.DB;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Revit
{
    /// <summary>
    /// Identity of an open document, computed the way a report records it (session token and model key),
    /// so <see cref="ReportDocumentMatch"/> can compare the two. Revit UI thread only.
    /// </summary>
    internal static class DocumentMatcher
    {
        internal static ActiveDocumentIdentity Identify(Document doc)
        {
            if (doc == null || !doc.IsValidObject) return null;
            var described = McpChangeTracker.DescribeForReport(doc);
            return new ActiveDocumentIdentity { Token = described.Token, ModelKey = described.ModelKey, Title = described.Title };
        }

        internal static bool Same(ActiveDocumentIdentity a, ActiveDocumentIdentity b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a.Token == b.Token && a.ModelKey == b.ModelKey && a.Title == b.Title;
        }
    }
}
