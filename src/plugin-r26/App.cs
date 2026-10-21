using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RvtMcp.Plugin.Views;
using RvtMcp.Plugin.Views.Toast;
using RvtMcp.Plugin.Views.Settings;

namespace RvtMcp.Plugin
{
    public class App : IExternalApplication
    {
        public static App Instance { get; private set; }

        public ITransportServer Transport { get; private set; }
        public bool IsTransportRunning => Transport?.IsRunning == true;
        public McpSessionLog SessionLog { get; private set; }
        public CommandDispatcher CommandDispatcher => _dispatcher;
        public ToolBaker.BakedToolRegistry BakedToolRegistry { get; private set; }
        public ToolBaker.BakedToolRuntimeCache BakedToolRuntimeCache { get; private set; }
        public RvtMcpConfig Config { get; private set; }
        public McpEventHandler EventHandler => _handler;
        public ExternalEvent ExternalEvent => _externalEvent;
        private bool _toastEnabled;
        public event EventHandler ToastEnabledChanged;
        public bool ToastEnabled
        {
            get => _toastEnabled;
            set
            {
                if (_toastEnabled == value) return;
                _toastEnabled = value;
                ToastEnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public McpToastNotifier ToastNotifier { get; private set; }

        private McpEventHandler _handler;
        private ExternalEvent _externalEvent;
        private CommandDispatcher _dispatcher;
        private IdlingUpdater _idlingUpdater;
        private HistoryWindow _historyWindow;
        private BakeInboxWindow _bakeInboxWindow;
        private SettingsWindow _settingsWindow;
        private UIControlledApplication _ribbonApplication;
        private bool _mainWindowCaptured;
        private System.Windows.Threading.Dispatcher _revitDispatcher;
        private Update.UpdateNoticeCoordinator _updateNotice;

        public Result OnStartup(UIControlledApplication application)
        {
            AuthToken.RevitVersion = "2026";
            Instance = this;

            DebugLog("OnStartup: BEGIN");
            HostHealthSnapshot.Initialize(typeof(App).Assembly, 2026);

            McpLogger.Initialize();
            if (!LegacyDataMigration.TryMigrateOnce(DebugLog)) return Result.Failed;
            DiagnosticLog.MaintainLegacyLogs();
            SessionLog = new McpSessionLog();
            Config = RvtMcpConfig.Load(args: null);
            Localization.LocalizationHost.InitializePlugin(
                application.ControlledApplication.Language.ToString(), Config.UiLanguage, DebugLog);
            ToastEnabled = Config.EnableToastOrDefault;
            _updateNotice = new Update.UpdateNoticeCoordinator(DebugLog);
            _updateNotice.Start();
            DebugLog("OnStartup: McpLogger + SessionLog OK");

            BakedToolRuntimeCache = new ToolBaker.BakedToolRuntimeCache();
            _dispatcher = new CommandDispatcher(BakedToolRuntimeCache);
            BakedToolRegistry = new ToolBaker.BakedToolRegistry();
            _dispatcher.LoadBakedTools(BakedToolRegistry);
            DebugLog("OnStartup: BakedToolRegistry loaded");
            var toastHost = new McpToastHost(() => Config?.ToastIdleSecondsOrDefault ?? RvtMcpConfig.DefaultToastIdleSeconds);
            toastHost.SetShowBranding(Config.ShowBrandingOrDefault);
            toastHost.SetPositionOptions(Config.ToastPositionOrDefault);
            ToastNotifier = new McpToastNotifier(toastHost, () => ToastEnabled);
            _handler = new McpEventHandler(_dispatcher, SessionLog, ToastNotifier);
            _externalEvent = ExternalEvent.Create(_handler);
            application.ControlledApplication.DocumentChanged += McpChangeTracker.OnDocumentChanged;
            Failures.FailureControl.Initialize(application);
            application.ViewActivated += Survey.SurveyHistoryBridge.Invalidate;
            application.ControlledApplication.DocumentClosing += Survey.SurveyHistoryBridge.Invalidate;
            application.ControlledApplication.DocumentSavedAs += Survey.SurveyHistoryBridge.Invalidate;
            DebugLog("OnStartup: Dispatcher + EventHandler + ExternalEvent OK");

            var otherMcps = McpAddinDiscovery.FindOtherMcpAddins(AuthToken.RevitVersion, DebugLog);
            if (Config.AutoStartTransportOrDefault && otherMcps.Count == 0)
            {
                try { CreateAndStartTransport(); DebugLog("OnStartup: Transport OK"); }
                catch (Exception ex) { DebugLog("OnStartup: MCP remains OFF: " + ex.Message); }
            }
            else
            {
                DebugLog(otherMcps.Count > 0 ? "OnStartup: MCP remains OFF because another third-party MCP add-in is installed" : "OnStartup: Transport stopped by startup preference");
            }

            _ribbonApplication = application;
            var ribbonResult = RibbonSetup.Create(application, Config, BakedToolRuntimeCache);
            DebugLog("OnStartup: RibbonSetup OK");

            _idlingUpdater = new IdlingUpdater(ribbonResult, ToastNotifier);
            application.Idling += OnIdling;
            DebugLog("OnStartup: END");

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            Failures.FailureControl.Shutdown(application);
            application.ControlledApplication.DocumentChanged -= McpChangeTracker.OnDocumentChanged;
            application.ViewActivated -= Survey.SurveyHistoryBridge.Invalidate;
            application.ControlledApplication.DocumentClosing -= Survey.SurveyHistoryBridge.Invalidate;
            application.ControlledApplication.DocumentSavedAs -= Survey.SurveyHistoryBridge.Invalidate;
            application.Idling -= OnIdling;
            Localization.LocalizationHost.ShutdownPlugin();
            _historyWindow?.CloseForShutdown();
            _bakeInboxWindow?.Close();
            _settingsWindow?.CloseForShutdown();
            _updateNotice?.Shutdown();

            try { StopTransport(); } catch (Exception ex) { DebugLog("Shutdown transport cleanup: " + ex.Message); }
            _handler?.CancelAll();
            ToastNotifier?.Shutdown();
            _externalEvent?.Dispose();

            Instance = null;
            return Result.Succeeded;
        }

        public void CreateAndStartTransport()
        {
            StopTransport();
            var pipe = new PipeTransportServer();
            pipe.Start((line, tcs) =>
            {
                // Parse JSON and enqueue to McpEventHandler
                Newtonsoft.Json.Linq.JObject request;
                try
                {
                    request = Newtonsoft.Json.Linq.JObject.Parse(line);
                }
                catch
                {
                    tcs.TrySetResult(Newtonsoft.Json.JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "Invalid JSON request."
                    }));
                    return;
                }

                var pending = new PendingRequest
                {
                    Id = request.Value<string>("id"),
                    CommandName = request.Value<string>("command"),
                    ParamsJson = request["params"]?.ToString() ?? "{}",
                    RuntimeOptions = request["runtime"]?.ToObject<RvtMcpConfig>(),
                    History = request["history"]?.ToObject<ChangeHistoryRequest>(),
                    FromMcp = true,
                    ExpectedDocumentId = request.Value<string>("expected_document_id"),
                    DocumentProtocol = request.Value<int?>("document_protocol") >= 1,
                    Tcs = tcs
                };

                _handler.Enqueue(pending);
                _externalEvent.Raise();
            });
            Transport = pipe;
        }

        public void StopTransport()
        {
            if (Transport != null)
            {
                Transport.Stop();
                Transport.Dispose();
                Transport = null;
            }
        }

        public void ShowOrFocusHistoryWindow()
        {
            if (_historyWindow == null || !_historyWindow.IsLoaded)
            {
                _historyWindow?.Dispose();
                _historyWindow = new HistoryWindow(SessionLog, _dispatcher, _handler, _externalEvent);
                var hwnd = _ribbonApplication?.MainWindowHandle ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(_historyWindow).Owner = hwnd;
                _historyWindow.Show();
            }
            else if (!_historyWindow.IsVisible)
            {
                _historyWindow.Show();
            }
            else
            {
                _historyWindow.Activate();
            }
        }

        public void ShowIssues()
        {
            ShowOrFocusHistoryWindow();
            _historyWindow?.ShowIssues();
        }

        public void ShowOrFocusSettingsWindow(SettingsTab initialTab = SettingsTab.General)
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(this, initialTab);
                var hwnd = _ribbonApplication?.MainWindowHandle ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(_settingsWindow).Owner = hwnd;
                _settingsWindow.Show();
            }
            else
            {
                _settingsWindow.SelectTab(initialTab);
                if (!_settingsWindow.IsVisible) _settingsWindow.Show();
                _settingsWindow.Activate();
            }
        }

        public void FocusSettingsLanguage()
        {
            _settingsWindow?.FocusLanguage();
        }

        public void ShowOrFocusBakeInboxWindow()
        {
            if (_bakeInboxWindow == null || !_bakeInboxWindow.IsLoaded)
                _bakeInboxWindow = new BakeInboxWindow(BakedToolRegistry, BakedToolRuntimeCache);
            _bakeInboxWindow.ShowOrFocus();
        }

        public void CaptureMainWindowHandle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;
            ToastNotifier?.SetOwnerHandle(hwnd);
            try
            {
                _revitDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                ToastNotifier?.SetHostDispatcher(_revitDispatcher);
            }
            catch { }
        }

        /// <summary>Open History on Revit's UI dispatcher when called by the toast window.</summary>
        public void ShowOrFocusHistoryWindowFromToast()
        {
            var dispatcher = _revitDispatcher;
            if (dispatcher == null)
            {
                DebugLog("Toast history click ignored: Revit dispatcher is not captured.");
                return;
            }

            if (dispatcher.CheckAccess())
                ShowOrFocusHistoryWindow();
            else
                dispatcher.BeginInvoke(new Action(ShowOrFocusHistoryWindow),
                    System.Windows.Threading.DispatcherPriority.Normal);
        }

        public void RefreshBakedRibbonButtons()
        {
            if (Config?.EnableAdaptiveBakeOrDefault == true && _ribbonApplication != null)
                RibbonSetup.AddOrUpdateBakedToolButtons(_ribbonApplication, BakedToolRuntimeCache);
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            Failures.FailureControl.OnIdling(sender as UIApplication);
            HostHealthObserver.Observe(sender as UIApplication);
            // Capture the owner early so pre-command toasts (e.g. agent
            // connected) anchor to the Revit window instead of the screen.
            if (!_mainWindowCaptured)
            {
                var hwnd = _ribbonApplication?.MainWindowHandle ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                {
                    CaptureMainWindowHandle(hwnd);
                    _mainWindowCaptured = true;
                }
            }
            _updateNotice?.OnIdling(_ribbonApplication?.MainWindowHandle ?? IntPtr.Zero);
            _idlingUpdater?.Update(IsTransportRunning, Transport, SessionLog, ToastEnabled);
        }

        internal static void DebugLog(string message)
        {
            DiagnosticLog.Write("App", message, AuthToken.RevitVersion);
        }
    }
}
