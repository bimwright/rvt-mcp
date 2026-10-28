using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Storage;
using RvtMcp.Plugin.Reports.Views;

namespace RvtMcp.Plugin.Reports.Revit
{
    /// <summary>
    /// Owns the Reports window and what it needs inside Revit: the runtime over the shared store, the active
    /// document tracker, the navigation ExternalEvent, the view model and the window. Revit UI thread only.
    /// </summary>
    internal static class ReportsModule
    {
        private static UIControlledApplication _application;
        private static RevitReportsRuntime _runtime;
        private static NavigationEventHandler _navigation;
        private static ActiveDocumentTracker _tracker;
        private static ReportsViewModel _viewModel;
        private static ReportsWindow _window;

        /// <summary>Add-in startup: a valid API context for the ExternalEvent. Old reports are removed in the background.</summary>
        internal static void Initialize(UIControlledApplication application, RvtMcpConfig config)
        {
            try
            {
                _application = application;
                _runtime = new RevitReportsRuntime(Dispatcher.CurrentDispatcher);
                _navigation = new NavigationEventHandler();
                _tracker = new ActiveDocumentTracker(application, _runtime.SetActiveDocument);
                var retention = TimeSpan.FromDays(config?.ReportRetentionDaysOrDefault ?? RvtMcpConfig.DefaultReportRetentionDays);
                Task.Run(() => ReportRetention.Cleanup(ReportStore.DefaultDirectory, retention, DateTime.UtcNow, App.DebugLog));
            }
            catch (Exception ex)
            {
                App.DebugLog("Reports: module failed to start: " + ex.Message);
                Shutdown();
            }
        }

        /// <summary>Create the window on first use, then show, restore and activate it.</summary>
        /// <param name="app">Given by the ribbon command, so the active document is re-read in a valid API context.</param>
        internal static void ShowOrFocus(UIApplication app = null)
        {
            if (_runtime == null) return;
            if (app != null) _tracker?.Refresh(app);
            if (_window == null)
            {
                var state = RvtMcpConfig.LoadReadOnly().ReportsWindowOrDefault;
                _viewModel = new ReportsViewModel(_runtime, _navigation,
                    state.Mode == ReportsWindowMode.Compact ? DisplayMode.Compact : DisplayMode.Wide);
                _window = new ReportsWindow(_viewModel, state, SaveState);
                var owner = _application?.MainWindowHandle ?? IntPtr.Zero;
                if (owner != IntPtr.Zero) new WindowInteropHelper(_window).Owner = owner;
                _window.Closed += OnWindowClosed;
                _window.Show();
            }
            else if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        /// <summary>Open the window on <paramref name="reportId"/>; the filter switches to all models when needed.</summary>
        internal static void ShowReport(string reportId)
        {
            ShowOrFocus();
            _viewModel?.ShowReport(reportId);
        }

        internal static void Shutdown()
        {
            _window?.CloseForShutdown();
            ReleaseWindow();
            _tracker?.Dispose();
            _navigation?.Dispose();
            _runtime?.Dispose();
            _tracker = null;
            _navigation = null;
            _runtime = null;
            _application = null;
        }

        private static void SaveState(ReportsWindowState state)
        {
            if (!RvtMcpConfig.TrySaveReportsWindow(state, out var error))
                App.DebugLog("Reports: cannot save the window state: " + error);
        }

        private static void OnWindowClosed(object sender, EventArgs e) => ReleaseWindow();

        private static void ReleaseWindow()
        {
            if (_window != null) _window.Closed -= OnWindowClosed;
            _window = null;
            _viewModel?.Dispose();
            _viewModel = null;
        }
    }
}
