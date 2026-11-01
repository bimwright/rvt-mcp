using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Views;
using RvtMcp.Plugin.Views.Toast;

/// <summary>Reports in memory; no store, watcher, clipboard or user profile access.</summary>
internal sealed class FakeReportsRuntime : IReportsRuntime
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly object _gate = new object();
    private List<AgentReport> _reports = new List<AgentReport>();

    public List<string> Deleted { get; } = new List<string>();
    public List<string> Copied { get; } = new List<string>();
    public bool CopyResult { get; set; } = true;
    public int LoadCount { get; private set; }
    public ActiveDocumentIdentity ActiveDocument { get; set; }
    public DateTime Now { get; set; } = DemoData.Now;
    public TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Local;
    public ReportHostStatus HostStatus { get; set; } = new ReportHostStatus(12345, true, "Pipe:synthetic-revit-12345");
    public event EventHandler HostStatusChanged;
    public void SetHost(ReportHostStatus status) { HostStatus = status; HostStatusChanged?.Invoke(this, EventArgs.Empty); }

    public event EventHandler ReportsChanged;
    public event EventHandler ActiveDocumentChanged;

    public List<AgentReport> Reports
    {
        get { lock (_gate) return _reports.ToList(); }
        set { lock (_gate) _reports = (value ?? new List<AgentReport>()).ToList(); }
    }

    public IReadOnlyList<AgentReport> LoadReports()
    {
        lock (_gate)
        {
            LoadCount++;
            return _reports.ToList();
        }
    }

    public Func<string, int, int, Newtonsoft.Json.Linq.JObject> HistoryReader { get; set; }
    public Newtonsoft.Json.Linq.JObject LoadHistory(string reportId, int callOffset, int elementOffset) => HistoryReader?.Invoke(reportId, callOffset, elementOffset) ?? new Newtonsoft.Json.Linq.JObject { ["status"] = "no_provenance", ["calls"] = new Newtonsoft.Json.Linq.JArray() };
    public void DeleteReport(string reportId)
    {
        lock (_gate) _reports.RemoveAll(r => r.ReportId == reportId);
        Deleted.Add(reportId);
    }

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public bool CopyText(string text)
    {
        Copied.Add(text);
        return CopyResult;
    }

    public void RaiseReportsChanged() => ReportsChanged?.Invoke(this, EventArgs.Empty);

    public void SetActive(ActiveDocumentIdentity active)
    {
        ActiveDocument = active;
        ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Records requests and answers with the scripted result, like the host's ExternalEvent would later.</summary>
internal sealed class FakeNavigation : INavigationService
{
    public List<ReportNavigationRequest> Requests { get; } = new List<ReportNavigationRequest>();
    public ReportNavigationResult Next { get; set; }
    public event EventHandler<ReportNavigationRequest> Started;
    public event EventHandler<ReportNavigationResult> Completed;

    public void Navigate(ReportNavigationRequest request)
    {
        Requests.Add(request);
        Started?.Invoke(this, request);
        if (Next != null) Completed?.Invoke(this, Next);
    }
}

/// <summary>Fixed owner and work area in DIPs; small enough to stay on a 1280×720 DIP desktop.</summary>
internal sealed class FakeScreen : IReportsScreen
{
    public ToastBounds? Owner { get; set; } = new ToastBounds(40, 20, 1200, 640);
    public ToastBounds Work { get; set; } = new ToastBounds(0, 0, 1280, 680);
    public ToastBounds GetWorkArea(Window window, ToastBounds? near) => Work;
    public ToastBounds? GetOwnerBounds(Window window) => Owner;
}
