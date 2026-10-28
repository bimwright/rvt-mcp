using System.Collections.Generic;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Changes
{
    public sealed class CommandChanges
    {
        public bool Incomplete { get; set; }
        public List<CommandDocumentChanges> Documents { get; } = new List<CommandDocumentChanges>();
    }

    public sealed class CommandDocumentChanges
    {
        public ReportDocument Document { get; set; }
        public bool Incomplete { get; set; }
        public List<ReportChangedElement> Added { get; } = new List<ReportChangedElement>();
        public List<ReportChangedElement> Modified { get; } = new List<ReportChangedElement>();
        public List<ReportChangedElement> Deleted { get; } = new List<ReportChangedElement>();
    }

    public sealed class ReportChangeSet
    {
        public List<ReportDocument> Documents { get; set; } = new List<ReportDocument>();
        public ReportChanges Changes { get; set; }
    }
}
