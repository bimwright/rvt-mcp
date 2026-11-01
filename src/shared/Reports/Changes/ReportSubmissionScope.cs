using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin.Reports.Changes
{
    public sealed class ReportSubmissionScope
    {
        public string Session { get; set; }
        public IReadOnlyList<string> CallIds { get; set; }
        public string Kind => CallIds != null ? "calls" : Session != null ? "client" : "process";
        public void Validate()
        {
            if (Session != null && !ReportCallContext.IsId(Session)) throw new ReportCaptureException("REPORT_SESSION_INVALID: Invalid client session.");
            if (CallIds != null && (CallIds.Count > 256 || CallIds.Any(id => !ReportCallContext.IsId(id)) || CallIds.Distinct().Count() != CallIds.Count))
                throw new ReportCaptureException("REPORT_CAPTURE_INVALID: Invalid or duplicate capture IDs.");
            if (CallIds != null && Session == null) throw new ReportCaptureException("REPORT_PROVENANCE_REQUIRED: Explicit captures require a provenance-capable gateway.");
        }
    }

    public sealed class ReportCaptureException : InvalidOperationException
    {
        public ReportCaptureException(string message) : base(message) { }
    }
}
