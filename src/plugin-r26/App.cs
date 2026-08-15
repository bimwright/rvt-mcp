using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RvtMcp.Plugin.Views;
using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin
{
    public class App : IExternalApplication
    {
        public static App Instance { get; private set; }

        public ITransportServer Transport { get; private set; }
        public bool IsTransportRunning { get; private set; }
        public McpSessionLog SessionLog { get; private set; }
        public CommandDispatcher CommandDispatcher => _dispatcher;
        public ToolBaker.BakedToolRegistry BakedToolRegistry { get; private set; }
        public ToolBaker.BakedToolRuntimeCache BakedToolRuntimeCache { get; private set; }
        public RvtMcpConfig Config { get; private set; }
        public McpEventHandler EventHandler => _handler;
        public ExternalEvent ExternalEvent => _externalEvent;
        public bool ToastEnabled { get; set; }
        public McpToastNotifier ToastNotifier { get; private set; }

        private McpEventHandler _handler;
        private ExternalEvent _externalEvent;
        private CommandDispatcher _dispatcher;
        private IdlingUpdater _idlingUpdater;
        private HistoryWindow _historyWindow;
        private BakeInboxWindow _bakeInboxWindow;
        private UIControlledApplication _ribbonApplication;

        public Result OnStartup(UIControlledApplication application)
        {
            AuthToken.RevitVersion = "2026";
            Instance = this;

            DebugLog("OnStartup: BEGIN");

            McpLogger.Initialize();
            LegacyDataMigration.MigrateOnce();
            SessionLog = new McpSessionLog();
            Config = RvtMcpConfig.Load(args: null);
            ToastEnabled = Config.EnableToastOrDefault;
            DebugLog("OnStartup: McpLogger + SessionLog OK");

            BakedToolRuntimeCache = new ToolBaker.BakedToolRuntimeCache();
            _dispatcher = new CommandDispatcher(BakedToolRuntimeCache);
            BakedToolRegistry = new ToolBaker.BakedToolRegistry();
            _dispatcher.LoadBakedTools(BakedToolRegistry);
            DebugLog("OnStartup: BakedToolRegistry loaded");
            var toastHost = new McpToastHost();
            ToastNotifier = new McpToastNotifier(toastHost, () => ToastEnabled);
            _handler = new McpEventHandler(_dispatcher, SessionLog, ToastNotifier);
            _externalEvent = ExternalEvent.Create(_handler);
            DebugLog("OnStartup: Dispatcher + EventHandler + ExternalEvent OK");

            CreateAndStartTransport();
            DebugLog("OnStartup: Transport OK");

            _ribbonApplication = application;
            var ribbonResult = RibbonSetup.Create(application, Config, BakedToolRuntimeCache);
            DebugLog("OnStartup: RibbonSetup OK");

            _idlingUpdater = new IdlingUpdater(ribbonResult);
            application.Idling += OnIdling;
            DebugLog("OnStartup: END");

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdling;
            _historyWindow?.Close();
            _bakeInboxWindow?.Close();

            StopTransport();
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
                    Tcs = tcs
                };

                _handler.Enqueue(pending);
                _externalEvent.Raise();
            });
            Transport = pipe;
            IsTransportRunning = true;
        }

        public void StopTransport()
        {
            if (Transport != null)
            {
                Transport.Stop();
                Transport.Dispose();
                Transport = null;
            }
            IsTransportRunning = false;
        }

        public void ShowOrFocusHistoryWindow()
        {
            if (_historyWindow == null || !_historyWindow.IsLoaded)
            {
                _historyWindow = new HistoryWindow(SessionLog, _dispatcher, _handler, _externalEvent);
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
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
                ToastNotifier?.SetHostDispatcher(dispatcher);
            }
            catch { }
        }

        public void RefreshBakedRibbonButtons()
        {
            if (Config?.EnableAdaptiveBakeOrDefault == true && _ribbonApplication != null)
                RibbonSetup.AddOrUpdateBakedToolButtons(_ribbonApplication, BakedToolRuntimeCache);
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            _idlingUpdater?.Update(IsTransportRunning, Transport, SessionLog, ToastEnabled);
        }

        internal static void DebugLog(string message)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RvtMcp");
                System.IO.Directory.CreateDirectory(dir);
                var logFile = System.IO.Path.Combine(dir, "debug.log");
                System.IO.File.AppendAllText(logFile,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
