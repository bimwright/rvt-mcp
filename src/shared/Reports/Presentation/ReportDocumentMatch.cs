using System;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public sealed class ActiveDocumentIdentity
    {
        public string Token { get; set; }
        public string ModelKey { get; set; }
        public string Title { get; set; }
    }

    public static class ReportDocumentMatch
    {
        public static bool Matches(ReportDocument doc, ActiveDocumentIdentity active)
        {
            if (doc == null || active == null) return false;
            return (doc.Token != null && string.Equals(doc.Token, active.Token, StringComparison.Ordinal))
                || (doc.ModelKey != null && string.Equals(doc.ModelKey, active.ModelKey, StringComparison.Ordinal));
        }
    }
}
