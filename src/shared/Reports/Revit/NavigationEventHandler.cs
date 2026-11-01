using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Reports.Navigation;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Revit
{
    /// <summary>
    /// Link clicks reach Revit through the module's own ExternalEvent, never the MCP command queue: no History
    /// entry, failure scope or change capture. Only the newest click waits; an older one is dropped.
    /// </summary>
    internal sealed class NavigationEventHandler : INavigationService, IExternalEventHandler, IDisposable
    {
        private readonly LatestRequestSlot<ReportNavigationRequest> _pending = new LatestRequestSlot<ReportNavigationRequest>();
        private ExternalEvent _event;
        private long _nextTraceId;

        /// <summary>Must run in a valid API context (add-in startup).</summary>
        public NavigationEventHandler()
        {
            _event = ExternalEvent.Create(this);
        }

        public event EventHandler<ReportNavigationRequest> Started;
        public event EventHandler<ReportNavigationResult> Completed;

        public void Navigate(ReportNavigationRequest request)
        {
            var externalEvent = _event;
            if (request == null || externalEvent == null) return;
            request.TraceId = Interlocked.Increment(ref _nextTraceId);
            request.QueuedTimestamp = Stopwatch.GetTimestamp();
            _pending.Put(request);
            Started?.Invoke(this, request);
            try
            {
                var raised = externalEvent.Raise();
                if (raised == ExternalEventRequest.Accepted || raised == ExternalEventRequest.Pending) return;
                App.DebugLog("Reports navigation " + request.TraceId + ": event request " + raised);
            }
            catch (Exception ex) { App.DebugLog("Reports: cannot request navigation: " + ex.Message); }
            _pending.Take();
            Completed?.Invoke(this, new ReportNavigationResult { Outcome = ReportNavigationOutcome.Failed });
        }

        public void Execute(UIApplication app)
        {
            var request = _pending.Take();
            if (request == null) return;
            var queueMs = (Stopwatch.GetTimestamp() - request.QueuedTimestamp) * 1000.0 / Stopwatch.Frequency;
            var clock = Stopwatch.StartNew();
            ReportNavigationResult result;
            try { result = ElementNavigator.Navigate(app, request); }
            catch (Exception ex)
            {
                App.DebugLog("Reports: navigation failed: " + ex);
                result = new ReportNavigationResult { Outcome = ReportNavigationOutcome.Failed };
            }
            App.DebugLog("Reports navigation " + request.TraceId + ": queue_ms=" + queueMs.ToString("F1", CultureInfo.InvariantCulture)
                + "; execute_ms=" + clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + "; outcome=" + result.Outcome);
            Completed?.Invoke(this, result);
        }

        public string GetName() => "Bimwright Reports navigation";

        public void Dispose()
        {
            _event?.Dispose();
            _event = null;
        }
    }
}
