using System;
using System.Collections.Generic;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public interface IReportsRuntime
    {
        IReadOnlyList<AgentReport> LoadReports();
        void DeleteReport(string reportId);
        Newtonsoft.Json.Linq.JObject LoadHistory(string reportId, int callOffset, int elementOffset);
        event EventHandler ReportsChanged;
        ActiveDocumentIdentity ActiveDocument { get; }
        event EventHandler ActiveDocumentChanged;
        void Post(Action action);
        bool CopyText(string text);
        DateTime Now { get; }
        TimeZoneInfo LocalTimeZone { get; }
        ReportHostStatus HostStatus { get; }
        event EventHandler HostStatusChanged;
    }
}
