using System;
using System.Collections.Generic;
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

        public RevitReportsRuntime(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _watcher = new ReportFolderWatcher(log: App.DebugLog);
            _watcher.Changed += OnFolderChanged;
        }

        public event EventHandler ReportsChanged;
        public event EventHandler ActiveDocumentChanged;

        public ActiveDocumentIdentity ActiveDocument => _active;
        public DateTime Now => DateTime.Now;

        public IReadOnlyList<AgentReport> LoadReports() => ReportServices.Store.LoadNewest();

        public void DeleteReport(string reportId) => ReportServices.Store.Delete(reportId);

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
