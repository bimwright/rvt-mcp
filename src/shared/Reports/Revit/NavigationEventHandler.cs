using System;
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

        /// <summary>Must run in a valid API context (add-in startup).</summary>
        public NavigationEventHandler()
        {
            _event = ExternalEvent.Create(this);
        }

        public event EventHandler<ReportNavigationResult> Completed;

        public void Navigate(ReportNavigationRequest request)
        {
            var externalEvent = _event;
            if (request == null || externalEvent == null) return;
            _pending.Put(request);
            externalEvent.Raise();
        }

        public void Execute(UIApplication app)
        {
            var request = _pending.Take();
            if (request == null) return;
            ReportNavigationResult result;
            try { result = ElementNavigator.Navigate(app, request); }
            catch (Exception ex)
            {
                App.DebugLog("Reports: navigation failed: " + ex);
                result = new ReportNavigationResult { Outcome = ReportNavigationOutcome.Failed };
            }
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
