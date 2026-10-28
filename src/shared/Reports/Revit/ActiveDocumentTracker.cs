using System;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Revit
{
    /// <summary>
    /// Follows the active document through view activation, open, close and Save As (a new path is a new
    /// model key) and reports each change of identity once. Events arrive on the Revit UI thread.
    /// </summary>
    internal sealed class ActiveDocumentTracker : IDisposable
    {
        private readonly UIControlledApplication _application;
        private readonly Action<ActiveDocumentIdentity> _changed;
        private ActiveDocumentIdentity _current;
        private bool _disposed;

        public ActiveDocumentTracker(UIControlledApplication application, Action<ActiveDocumentIdentity> changed)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
            _changed = changed ?? throw new ArgumentNullException(nameof(changed));
            application.ViewActivated += OnViewActivated;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            application.ControlledApplication.DocumentClosed += OnDocumentClosed;
            application.ControlledApplication.DocumentSavedAs += OnDocumentSavedAs;
        }

        /// <summary>Re-read the active document from a valid API context, such as a ribbon command.</summary>
        public void Refresh(UIApplication app) => Update(app?.ActiveUIDocument?.Document);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _application.ViewActivated -= OnViewActivated;
            _application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            _application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
            _application.ControlledApplication.DocumentSavedAs -= OnDocumentSavedAs;
        }

        private void OnViewActivated(object sender, ViewActivatedEventArgs e) => Update(e.Document);
        private void OnDocumentOpened(object sender, DocumentOpenedEventArgs e) => UpdateFrom(sender);
        // After the last document closes there is no active document and no further ViewActivated.
        private void OnDocumentClosed(object sender, DocumentClosedEventArgs e) => UpdateFrom(sender);
        private void OnDocumentSavedAs(object sender, DocumentSavedAsEventArgs e) => UpdateFrom(sender);

        private void UpdateFrom(object sender)
        {
            if (!(sender is Application app)) return;
            Update(new UIApplication(app).ActiveUIDocument?.Document);
        }

        private void Update(Document doc)
        {
            if (_disposed) return;
            ActiveDocumentIdentity identity;
            try { identity = DocumentMatcher.Identify(doc); }
            catch (Exception ex)
            {
                App.DebugLog("Reports: cannot identify the active document: " + ex.Message);
                return;
            }
            if (DocumentMatcher.Same(identity, _current)) return;
            _current = identity;
            _changed(identity);
        }
    }
}
