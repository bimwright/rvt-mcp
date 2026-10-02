using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// Reconciles the WPF toast surface with the pure <see cref="ActivityAggregator"/>
    /// state machine. There is deliberately one window slot: the aggregator owns the
    /// card lifetime and the manager owns only WPF objects and their dispatcher timer.
    /// </summary>
    internal sealed class McpToastManager
    {
        private const double EdgeMargin = 16;
        private const int TickMilliseconds = 100;

        private readonly Dispatcher _dispatcher;
        private readonly ActivityAggregator _aggregator;
        private readonly Func<bool> _isFrameUsable;
        private readonly Action<long> _onClick;
        private readonly Func<bool> _showBranding;
        private readonly Func<string> _instanceIdentity;
        private readonly Func<bool> _motionEnabled;
        private readonly DispatcherTimer _timer;
        private McpToastWindow _window;
        private IntPtr _ownerHandle;

        /// <param name="isFrameUsable">
        /// Returns whether the owner frame can display an activity card. The callback is
        /// evaluated on the toast dispatcher by the timer; it must be cheap and must not
        /// call back into this manager. A missing callback means that the frame is usable.
        /// </param>
        /// <param name="instanceIdentity">
        /// Card title identifying this Revit instance so cards from parallel
        /// Revit processes can be told apart. Evaluated when each card is
        /// created; null/empty falls back to the product name.
        /// </param>
        /// <param name="motionEnabled">
        /// Passed to each card window; null follows the Windows animation setting.
        /// </param>
        public McpToastManager(
            Dispatcher dispatcher,
            ActivityAggregator aggregator,
            Func<bool> isFrameUsable = null,
            Action<long> onClick = null,
            Func<bool> showBranding = null,
            Func<string> instanceIdentity = null,
            Func<bool> motionEnabled = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
            _isFrameUsable = isFrameUsable ?? (() => true);
            _onClick = onClick;
            _showBranding = showBranding ?? (() => true);
            _instanceIdentity = instanceIdentity ?? (() => null);
            _motionEnabled = motionEnabled;

            _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(TickMilliseconds)
            };
            _timer.Tick += OnTimerTick;
            L.Changed += OnLanguageChanged;
        }

        public void SetOwnerHandle(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
                _ownerHandle = hwnd;
        }

        /// <summary>Apply the current branding preference to the open card, if any.</summary>
        public void ApplyShowBranding()
        {
            EnsureDispatcher();
            _window?.SetShowBranding(_showBranding());
        }

        /// <summary>
        /// Reconciles the single WPF window to the aggregator's current render state.
        /// Call this after a mutator returns true. Calling it more often is safe because
        /// <see cref="ActivityAggregator.TakeRender"/> always reads current state.
        /// </summary>
        public void Render()
        {
            EnsureDispatcher();

            var render = _aggregator.TakeRender();
            switch (render.Phase)
            {
                case ActivityCardPhase.Visible:
                    ReconcileVisible(render.Card);
                    return;

                case ActivityCardPhase.Closing:
                    ReconcileClosing(render.Card);
                    return;

                default:
                    ForceCloseWindow();
                    StopTimerIfNoWindow();
                    return;
            }
        }

        /// <summary>
        /// Allows the host/Idling path to supply an explicit frame usability result.
        /// The timer uses the configured callback and calls this overload internally.
        /// </summary>
        internal void Tick(bool frameUsable)
        {
            EnsureDispatcher();
            if (_window == null)
                return;

            if (_aggregator.Tick(frameUsable))
                Render();
        }

        /// <summary>
        /// Closes the current window synchronously. This is used during Revit shutdown,
        /// before the dispatcher is torn down, and is also safe for a normal dismiss-all.
        /// </summary>
        public void DismissAllImmediate()
        {
            EnsureDispatcher();

            _timer.Stop();
            var window = _window;
            _window = null; // Ignore the close callback from this forced close.

            if (window != null)
            {
                try { window.CloseImmediate(); }
                catch { }
            }

            // A dismissed card must not be resurrected by a render that was posted
            // before DismissAllImmediate ran. Reset is idempotent and clears Pending too.
            _aggregator.Reset();
            // Reset claims a render request so a concurrent/late notifier cannot leave
            // the coalescing flag stuck with no posted render left to drain it.
            _aggregator.TakeRender();
        }

        /// <summary>Detach the global localization subscription before the host dies.</summary>
        public void Dispose()
        {
            L.Changed -= OnLanguageChanged;
            if (_dispatcher.CheckAccess())
                _timer.Stop();
        }

        private void ReconcileVisible(ActivitySnapshot card)
        {
            if (card == null)
            {
                ForceCloseWindow();
                StopTimerIfNoWindow();
                return;
            }

            if (_window != null && _window.CardId == card.CardId)
            {
                // Updating an existing card must not replay its enter/brand animation or
                // alter its measured height.
                _window.Update(card);
                EnsureTimer();
                return;
            }

            // A result arriving while the old card fades starts a new CardId. Force-close
            // the old HWND first so there can never be two topmost windows.
            ForceCloseWindow();
            CreateWindow(card);
        }

        private void ReconcileClosing(ActivitySnapshot card)
        {
            if (card == null)
            {
                StopTimerIfNoWindow();
                return;
            }

            if (_window != null && _window.CardId == card.CardId)
            {
                // BeginClose is idempotent in the window. Repeated stale renders cannot
                // restart or reverse the fade.
                _window.BeginClose();
                EnsureTimer();
                return;
            }

            // Pending render for a card whose window was already closed (or replaced).
            // Complete the aggregator transition immediately; a late callback from an
            // older window is ignored by OnWindowClosed's identity + CardId fence.
            ForceCloseWindow();
            _aggregator.CardClosed(card.CardId);
            StopTimerIfNoWindow();
        }

        private void CreateWindow(ActivitySnapshot card)
        {
            var window = new McpToastWindow(
                card,
                OnWindowClosed,
                OnDismissRequested,
                OnCardClicked,
                OnPointerEntered,
                OnPointerLeft,
                motionEnabled: _motionEnabled,
                instanceIdentity: _instanceIdentity());

            _window = window;
            window.SetShowBranding(_showBranding());
            AttachOwner(window);

            // WPF initializes Window.Top/Left to NaN. Set finite coordinates before Show
            // so an early Loaded/close callback cannot animate from an invalid value.
            PositionWindow(window);
            window.CapturePointerBaseline();
            window.Show();
            window.PlayEnterAnimation();
            EnsureTimer();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_window == null)
            {
                _timer.Stop();
                return;
            }

            bool frameUsable;
            try { frameUsable = _isFrameUsable(); }
            catch { frameUsable = true; }
            Tick(frameUsable);
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                return;

            void Refresh()
            {
                if (_window != null)
                    _window.RefreshLocalization();
            }

            try
            {
                if (_dispatcher.CheckAccess())
                    Refresh();
                else
                    _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(Refresh));
            }
            catch
            {
                // The watcher can race Revit/WPF shutdown. A stale toast is safer
                // than surfacing an exception from a best-effort localization refresh.
            }
        }

        private void OnWindowClosed(McpToastWindow window, long cardId)
        {
            EnsureDispatcher();
            if (!ReferenceEquals(_window, window) || window.CardId != cardId)
                return;

            _window = null;
            _aggregator.CardClosed(cardId);
            StopTimerIfNoWindow();
        }

        private void OnDismissRequested(long cardId)
        {
            EnsureDispatcher();
            if (_window == null || _window.CardId != cardId)
                return;

            if (_aggregator.Dismiss(cardId))
                Render();
        }

        private void OnPointerEntered(long cardId)
        {
            EnsureDispatcher();
            if (_window != null && _window.CardId == cardId && _aggregator.PointerEntered(cardId))
                Render();
        }

        private void OnPointerLeft(long cardId)
        {
            EnsureDispatcher();
            if (_window != null && _window.CardId == cardId)
                _aggregator.PointerLeft(cardId);
        }

        private void ForceCloseWindow()
        {
            var window = _window;
            if (window == null)
                return;

            _window = null;
            try { window.CloseImmediate(); }
            catch { }
            StopTimerIfNoWindow();
        }

        private void OnCardClicked(long cardId)
        {
            EnsureDispatcher();
            if (_window == null || _window.CardId != cardId)
                return;

            // Opening History is intentionally supplied by the host, because App.Instance
            // lives in each Revit-year shell and is absent from the WPF harness.
            try { _onClick?.Invoke(cardId); }
            catch { }

            if (_aggregator.Dismiss(cardId))
                Render();
        }

        private void EnsureTimer()
        {
            if (!_timer.IsEnabled && _window != null)
                _timer.Start();
        }

        private void StopTimerIfNoWindow()
        {
            if (_window == null)
                _timer.Stop();
        }

        private void AttachOwner(McpToastWindow window)
        {
            var owner = GetValidOwnerHandle();
            if (owner == IntPtr.Zero)
                return;

            try
            {
                new WindowInteropHelper(window).Owner = owner;
            }
            catch
            {
                // Best-effort — positioning still works without WPF ownership.
            }
        }

        private void PositionWindow(McpToastWindow window)
        {
            var owner = GetValidOwnerHandle();
            double left = EdgeMargin;
            double top = EdgeMargin;

            if (owner != IntPtr.Zero && GetWindowRect(owner, out var rect))
            {
                GetOwnerDpiScale(owner, out var dpiX, out var dpiY);
                left = rect.Left * dpiX + EdgeMargin;
                top = rect.Top * dpiY + EdgeMargin;
            }

            // Guard both the native and fallback paths. Invalid DPI/rect data must never
            // leak NaN or infinity into WPF dependency properties.
            if (!IsFinite(left)) left = EdgeMargin;
            if (!IsFinite(top)) top = EdgeMargin;
            window.SetPosition(top, left);
        }

        private IntPtr GetValidOwnerHandle()
        {
            if (_ownerHandle != IntPtr.Zero && IsWindow(_ownerHandle))
                return _ownerHandle;

            // Keep the last known good owner rather than guessing a process main window;
            // clear only when the native handle is truly invalid.
            if (_ownerHandle != IntPtr.Zero && !IsWindow(_ownerHandle))
                _ownerHandle = IntPtr.Zero;

            return _ownerHandle;
        }

        private void GetOwnerDpiScale(IntPtr hwnd, out double dpiX, out double dpiY)
        {
            dpiX = 1.0;
            dpiY = 1.0;

            try
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    dpiX = 96.0 / dpi;
                    dpiY = dpiX;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Older Windows — retain the 96 DPI default.
            }
            catch
            {
                // DPI is a positioning hint; retain the safe default on failure.
            }
        }

        private void EnsureDispatcher()
        {
            if (!_dispatcher.CheckAccess())
                throw new InvalidOperationException("McpToastManager must run on the toast dispatcher thread.");
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }
    }
}
