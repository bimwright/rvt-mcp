using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Storage;

namespace RvtMcp.Plugin.Reports.Revit
{
    /// <summary>
    /// The Reports view model's view of Revit: the shared report store and its folder watcher, the active
    /// document identity pushed by <see cref="ActiveDocumentTracker"/>, Revit's WPF dispatcher and the clipboard.
    /// </summary>
    internal sealed class RevitReportsRuntime : IReportsRuntime, IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly ReportFolderWatcher _watcher;
        private volatile ActiveDocumentIdentity _active;
        private static readonly int OwnerPid = CapturePid();
        private ReportHostStatus _host = CaptureHost();
        private static int CapturePid() { using (var process = Process.GetCurrentProcess()) return process.Id; }

        public RevitReportsRuntime(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _watcher = new ReportFolderWatcher(log: App.DebugLog);
            _watcher.Changed += OnFolderChanged;
        }

        public event EventHandler ReportsChanged;
        public event EventHandler ActiveDocumentChanged;
        public event EventHandler HostStatusChanged;
        public ReportHostStatus HostStatus => _host;
        private static ReportHostStatus CaptureHost() => new ReportHostStatus(OwnerPid,
            App.Instance?.IsTransportRunning == true, App.Instance?.Transport?.ConnectionInfo);
        internal void RefreshHostStatus()
        {
            var next = CaptureHost();
            if (_host.Pid == next.Pid && _host.Running == next.Running && _host.Transport == next.Transport && _host.Endpoint == next.Endpoint) return;
            _host = next;
            HostStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public ActiveDocumentIdentity ActiveDocument => _active;
        public DateTime Now => DateTime.Now;
        public TimeZoneInfo LocalTimeZone
        {
            get
            {
                // A visible Reports window must notice an OS time-zone change without restarting Revit.
                TimeZoneInfo.ClearCachedData();
                return TimeZoneInfo.Local;
            }
        }

        public IReadOnlyList<AgentReport> LoadReports() => ReportServices.Store.LoadNewest();

        public void DeleteReport(string reportId) => ReportServices.Store.Delete(reportId);
        public Newtonsoft.Json.Linq.JObject LoadHistory(string reportId, int callOffset, int elementOffset)
            => new History.ReportHistoryLookup().Read(reportId, callOffset, elementOffset, 1, 10);

        public void Post(Action action)
        {
            if (action == null || _dispatcher.HasShutdownStarted) return;
            _dispatcher.BeginInvoke(action);
        }

        public bool CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text ?? "");
                return true;
            }
            catch (Exception ex) // Another program can hold the clipboard open.
            {
                App.DebugLog("Reports: copy to clipboard failed: " + ex.Message);
                return false;
            }
        }

        internal void SetActiveDocument(ActiveDocumentIdentity identity)
        {
            _active = identity;
            ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _watcher.Changed -= OnFolderChanged;
            _watcher.Dispose();
        }

        private void OnFolderChanged(object sender, EventArgs e) => ReportsChanged?.Invoke(this, EventArgs.Empty);
    }
}
