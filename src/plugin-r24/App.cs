using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Bimwright.Rvt.Plugin.Views;

namespace Bimwright.Rvt.Plugin
{
    public class App : IExternalApplication
    {
        public static App Instance { get; private set; }

        public ITransportServer Transport { get; private set; }
        public bool IsTransportRunning { get; private set; }
        public McpSessionLog SessionLog { get; private set; }
        public CommandDispatcher CommandDispatcher => _dispatcher;
        public ToolBaker.BakedToolRegistry BakedToolRegistry { get; private set; }
        public McpEventHandler EventHandler => _handler;
        public ExternalEvent ExternalEvent => _externalEvent;

        private McpEventHandler _handler;
        private ExternalEvent _externalEvent;
        private CommandDispatcher _dispatcher;
        private IdlingUpdater _idlingUpdater;
        private HistoryWindow _historyWindow;

        public Result OnStartup(UIControlledApplication application)
        {
            AuthToken.RevitVersion = "R24";
            Instance = this;

            DebugLog("OnStartup: BEGIN");

            McpLogger.Initialize();
            SessionLog = new McpSessionLog();
            DebugLog("OnStartup: McpLogger + SessionLog OK");

            _dispatcher = new CommandDispatcher();
#if ALLOW_SEND_CODE
            BakedToolRegistry = new ToolBaker.BakedToolRegistry();
            _dispatcher.LoadBakedTools(BakedToolRegistry);
            DebugLog("OnStartup: BakedToolRegistry loaded");
#endif
            _handler = new McpEventHandler(_dispatcher, SessionLog);
            _externalEvent = ExternalEvent.Create(_handler);
            DebugLog("OnStartup: Dispatcher + EventHandler + ExternalEvent OK");

            CreateAndStartTransport();
            DebugLog("OnStartup: Transport OK");

            var ribbonResult = RibbonSetup.Create(application);
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

            StopTransport();
            _handler?.CancelAll();
            _externalEvent?.Dispose();

            Instance = null;
            return Result.Succeeded;
        }

        public void CreateAndStartTransport()
        {
            StopTransport();
            var tcp = new TcpTransportServer();
            tcp.Start((line, tcs) =>
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
            Transport = tcp;
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

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            _idlingUpdater?.Update(IsTransportRunning, Transport, SessionLog);
        }

        internal static void DebugLog(string message)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bimwright");
                System.IO.Directory.CreateDirectory(dir);
                var logFile = System.IO.Path.Combine(dir, "debug.log");
                System.IO.File.AppendAllText(logFile,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
