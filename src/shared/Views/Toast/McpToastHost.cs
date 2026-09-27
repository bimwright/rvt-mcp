using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace RvtMcp.Plugin.Views.Toast
{
    public sealed class McpToastHost
    {
        /// <summary>
        /// The state machine is owned by the host so the notifier and the WPF manager
        /// always reconcile the same card, even when a render is posted from another
        /// thread.  It is deliberately independent of WPF and uses its monotonic clock.
        /// </summary>
        internal ActivityAggregator Aggregator { get; }

        public McpToastHost(Func<int> idleSeconds = null)
        {
            // The provider is read when an activity card re-arms, so a Settings apply
            // takes effect on the next tool result without rebuilding the host.
            Aggregator = new ActivityAggregator(idleSeconds ?? (() => ActivityAggregator.DefaultIdleSeconds));
        }

        private readonly object _lock = new object();
        private Thread _thread;
        private volatile Dispatcher _dispatcher;
        private volatile McpToastManager _manager;
        private volatile Dispatcher _hostDispatcher;
        private IntPtr _ownerHandle;
        private bool _shutdownRequested;
        private bool _usesDedicatedThread;
        private Application _toastApplication;
        private Func<bool> _frameUsable = () => true;
        private Action<long> _activityClick = _ => { };
        private volatile bool _showBranding;
        private volatile string _instanceIdentity;

        /// <summary>
        /// Wordmark and product prefix on the activity card. Off until turned on.
        /// Session-only: a Revit restart clears it, and nothing is written to config.
        /// </summary>
        public bool ShowBranding => _showBranding;

        public void SetShowBranding(bool show)
        {
            _showBranding = show;
            PostToManager(manager => manager.ApplyShowBranding());
        }

        /// <summary>
        /// Instance footer label (e.g. "Revit 2027"). Read when each card is
        /// created, so a late or changed value applies to the next card.
        /// </summary>
        public void SetInstanceIdentity(string identity)
        {
            _instanceIdentity = string.IsNullOrWhiteSpace(identity) ? null : identity;
        }

        internal void SetFrameUsableProvider(Func<bool> provider)
        {
            _frameUsable = provider ?? (() => true);
        }

        internal void SetActivityClickHandler(Action<long> handler)
        {
            _activityClick = handler ?? (_ => { });
        }

        public void SetHostDispatcher(Dispatcher dispatcher)
        {
            if (dispatcher == null)
                return;
            _hostDispatcher = dispatcher;
        }

        public void SetOwnerHandle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;
            _ownerHandle = hwnd;
            PostToManager(manager => manager.SetOwnerHandle(hwnd));
        }

        public void EnsureStarted()
        {
            lock (_lock)
            {
                if (_dispatcher != null || _shutdownRequested)
                    return;

                // Revit (and most WPF hosts) already own Application.Current in this AppDomain.
                // Creating Window on a second STA thread in that case is unstable — use the host dispatcher.
                var hostDispatcher = _hostDispatcher ?? Application.Current?.Dispatcher;
                if (hostDispatcher != null)
                {
                    _dispatcher = hostDispatcher;
                    _manager = new McpToastManager(_dispatcher, Aggregator, () => _frameUsable(), id => _activityClick(id), () => _showBranding, () => _instanceIdentity);
                    if (_ownerHandle != IntPtr.Zero)
                        _manager.SetOwnerHandle(_ownerHandle);
                    _usesDedicatedThread = false;
                    App.DebugLog("McpToastHost: using host WPF dispatcher");
                    return;
                }

                var started = new ManualResetEventSlim(false);
                Exception startupError = null;

                _thread = new Thread(() =>
                {
                    try
                    {
                        if (Application.Current == null)
                        {
                            _toastApplication = new Application
                            {
                                ShutdownMode = ShutdownMode.OnExplicitShutdown
                            };
                        }

                        _dispatcher = Dispatcher.CurrentDispatcher;
                        _manager = new McpToastManager(_dispatcher, Aggregator, () => _frameUsable(), id => _activityClick(id), () => _showBranding, () => _instanceIdentity);
                        if (_ownerHandle != IntPtr.Zero)
                            _manager.SetOwnerHandle(_ownerHandle);
                    }
                    catch (Exception ex)
                    {
                        startupError = ex;
                        App.DebugLog("McpToastHost dedicated-thread startup failed: " + ex.Message);
                    }
                    finally
                    {
                        started.Set();
                    }

                    if (startupError != null)
                        return;

                    try
                    {
                        Dispatcher.Run();
                    }
                    finally
                    {
                        if (_toastApplication != null)
                        {
                            try { _toastApplication.Shutdown(); }
                            catch { }
                            _toastApplication = null;
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "RvtMcp.Toast"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                started.Wait(TimeSpan.FromSeconds(5));
                _usesDedicatedThread = startupError == null && _dispatcher != null;

                if (startupError != null)
                    _dispatcher = null;
                else
                    App.DebugLog("McpToastHost: using dedicated STA thread");
            }
        }

        internal void Post(Action<McpToastManager> action)
        {
            if (_shutdownRequested || action == null)
                return;

            EnsureStarted();
            PostToManager(action);
        }

        public void Post(Action action)
        {
            if (_shutdownRequested || action == null)
                return;

            EnsureStarted();
            var dispatcher = _dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;

            dispatcher.BeginInvoke(action);
        }

        /// <summary>
        /// Dismiss all toasts. Prefer synchronous invoke during shutdown so Topmost
        /// windows are closed before Revit tears down the dispatcher.
        /// </summary>
        public void DismissAll(bool synchronous = false)
        {
            if (_shutdownRequested && !synchronous)
                return;

            var dispatcher = _dispatcher;
            var manager = _manager;
            if (dispatcher == null || manager == null || dispatcher.HasShutdownStarted)
                return;

            void Run()
            {
                try { manager.DismissAllImmediate(); }
                catch (Exception ex) { App.DebugLog("McpToastHost dismiss failed: " + ex); }
            }

            if (synchronous)
            {
                try
                {
                    if (dispatcher.CheckAccess())
                        Run();
                    else
                        dispatcher.Invoke(Run, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    App.DebugLog("McpToastHost sync dismiss failed: " + ex.Message);
                }
                return;
            }

            PostToManager(m => m.DismissAllImmediate());
        }

        public void Shutdown()
        {
            // Close windows before flipping the shutdown flag so DismissAll can still run.
            DismissAll(synchronous: true);

            var manager = _manager;
            if (manager != null)
            {
                try
                {
                    // Unsubscribing is thread-safe and must still happen if the
                    // dispatcher has already started shutting down. Dispose stops
                    // the timer only when called on its owning dispatcher.
                    manager.Dispose();
                }
                catch (Exception ex)
                {
                    App.DebugLog("McpToastHost localization cleanup failed: " + ex.Message);
                }
            }

            lock (_lock)
            {
                _shutdownRequested = true;
            }

            if (!_usesDedicatedThread)
                return;

            var dedicatedDispatcher = _dispatcher;
            if (dedicatedDispatcher == null || dedicatedDispatcher.HasShutdownStarted)
                return;

            try
            {
                dedicatedDispatcher.InvokeShutdown();
            }
            catch
            {
                // Best-effort shutdown during Revit exit.
            }

            var thread = _thread;
            if (thread != null && thread.IsAlive)
                thread.Join(TimeSpan.FromSeconds(2));
        }

        private void PostToManager(Action<McpToastManager> action)
        {
            var dispatcher = _dispatcher;
            var manager = _manager;
            if (dispatcher == null || manager == null || dispatcher.HasShutdownStarted)
                return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    action(manager);
                }
                catch (Exception ex)
                {
                    App.DebugLog("McpToastHost action failed: " + ex);
                }
            }));
        }
    }
}
