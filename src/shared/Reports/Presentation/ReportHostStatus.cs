using System;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public sealed class ReportHostStatus
    {
        public ReportHostStatus(int pid, bool running, string connectionInfo)
        {
            Pid = pid;
            Running = running;
            var parts = (connectionInfo ?? "").Split(new[] { ':' }, 2);
            Transport = parts.Length == 2 && (parts[0] == "Pipe" || parts[0] == "TCP") ? parts[0] : null;
            Endpoint = running && Transport != null && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
        }
        public int Pid { get; }
        public bool Running { get; }
        public string Transport { get; }
        public string Endpoint { get; }
        public string Display => "Process: " + Pid + " - " + (Endpoint != null ? Transport + ": " + Endpoint
            : Localization.L.T(Running ? "reports.host.unavailable" : "reports.host.stopped"));
    }
}
