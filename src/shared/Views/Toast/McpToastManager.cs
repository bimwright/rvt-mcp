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
        private readonly Func<ToastPositionOptions> _positionOptions;
        private readonly Action<ToastPositionOptions> _positionChanged;
        private readonly Func<Tuple<ToastBounds, ToastBounds>> _geometry;
        private readonly Func<Point> _cursorPosition;
        private double _compactHeight;
        private bool _growUp;

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
            Func<bool> motionEnabled = null,
            Func<ToastPositionOptions> positionOptions = null,
            Action<ToastPositionOptions> positionChanged = null,
            Func<Tuple<ToastBounds, ToastBounds>> geometry = null,
            Func<Point> cursorPosition = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
            _isFrameUsable = isFrameUsable ?? (() => true);
            _onClick = onClick;
            _showBranding = showBranding ?? (() => true);
            _instanceIdentity = instanceIdentity ?? (() => null);
            _motionEnabled = motionEnabled;
            _positionOptions = positionOptions ?? (() => new ToastPositionOptions());
            _positionChanged = positionChanged;
            _geometry = geometry;
            _cursorPosition = cursorPosition;

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

        /// <summary>Re-anchor the open card after the corner, drag or offset preference changed.</summary>
        public void ApplyPosition()
        {
            EnsureDispatcher();
            if (_window == null)
                return;
            _window.CancelHeaderDrag();
            _growUp = _positionOptions().Bottom;
            PositionWindow(_window, animate: true);
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
            // Follow the owner when it moves or resizes, except while the user is dragging.
            if (_window != null && !_window.IsDragging && !_window.IsPositionAnimating)
                PositionWindow(_window);
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
                window.SizeChanged -= OnWindowSizeChanged;
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
                cursorPosition: _cursorPosition,
                instanceIdentity: _instanceIdentity());

            _window = window;
            window.SetShowBranding(_showBranding());
            var surface = (FrameworkElement)window.Content;
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _compactHeight = surface.DesiredSize.Height;
            _growUp = _positionOptions().Bottom;
            window.PrepareExpansion = extra => PrepareExpansion(window, extra);
            window.ConstrainDrag = point => ConstrainDrag(window, point);
            window.DragCompleted = point => SaveDrag(window, point);
            window.SizeChanged += OnWindowSizeChanged;
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

            window.SizeChanged -= OnWindowSizeChanged;
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
            window.SizeChanged -= OnWindowSizeChanged;
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

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ReferenceEquals(sender, _window) && !_window.IsDragging)
                PositionWindow(_window);
        }

        /// <summary>Owner rectangle and the work area of the monitor holding it, both in DIPs.</summary>
        private Tuple<ToastBounds, ToastBounds> GetGeometry()
        {
            if (_geometry != null)
                return _geometry();

            var owner = GetValidOwnerHandle();
            var work = SystemParameters.WorkArea;
            var ownerBounds = new ToastBounds(0, 0, work.Width, work.Height);
            var workBounds = new ToastBounds(work.Left, work.Top, work.Width, work.Height);
            if (owner != IntPtr.Zero && GetWindowRect(owner, out var rect))
            {
                GetOwnerDpiScale(owner, out var x, out var y);
                ownerBounds = new ToastBounds(rect.Left * x, rect.Top * y,
                    (rect.Right - rect.Left) * x, (rect.Bottom - rect.Top) * y);
                var monitor = MonitorFromWindow(owner, 2);
                var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    workBounds = new ToastBounds(info.Work.Left * x, info.Work.Top * y,
                        (info.Work.Right - info.Work.Left) * x, (info.Work.Bottom - info.Work.Top) * y);
            }
            return Tuple.Create(ownerBounds, workBounds);
        }

        private static double WindowWidth(McpToastWindow window) =>
            window.ActualWidth > 0 ? window.ActualWidth : ((FrameworkElement)window.Content).DesiredSize.Width;

        private static double WindowHeight(McpToastWindow window) =>
            window.ActualHeight > 0 ? window.ActualHeight : ((FrameworkElement)window.Content).DesiredSize.Height;

        private void PositionWindow(McpToastWindow window, bool animate = false)
        {
            if (window.IsDragging)
                return;

            var options = _positionOptions();
            window.SetPositionPreferences(options.DragEnabled, options.Right, _growUp);
            var geometry = GetGeometry();
            var width = WindowWidth(window);
            var height = WindowHeight(window);
            var basis = _compactHeight > 0 ? _compactHeight : height;

            // Anchor with the compact height so the fixed edge stays put while the card
            // grows; growing upward then lifts the top by the extra height. Non-finite
            // owner/DPI data is replaced by the work-area corner inside Clamp.
            var anchor = ToastPlacement.Clamp(ToastPlacement.Anchor(geometry.Item1, width, basis, options), geometry.Item2);
            var bounds = ToastPlacement.Clamp(new ToastBounds(anchor.Left,
                anchor.Top - (_growUp ? height - basis : 0), width, height), geometry.Item2);

            // Height may still be animating when a corner changes. Retarget the move
            // from its current position instead of cancelling it with a jump.
            if (animate || window.IsPositionAnimating)
                window.MoveTo(bounds.Top, bounds.Left);
            else if (!IsFinite(window.Top) || !IsFinite(window.Left)
                || Math.Abs(window.Top - bounds.Top) > .1 || Math.Abs(window.Left - bounds.Left) > .1)
                window.SetPosition(bounds.Top, bounds.Left);
        }

        private void PrepareExpansion(McpToastWindow window, double extra)
        {
            if (!ReferenceEquals(window, _window) || window.IsDragging || window.HasExpandedRows)
                return;
            var geometry = GetGeometry();
            var current = new ToastBounds(window.Left, window.Top, WindowWidth(window), WindowHeight(window));
            _growUp = ToastPlacement.GrowUp(current, geometry.Item2, extra, _positionOptions().Bottom);
            PositionWindow(window);
        }

        private Point ConstrainDrag(McpToastWindow window, Point point)
        {
            var bounds = ToastPlacement.Clamp(new ToastBounds(point.X, point.Y, WindowWidth(window), WindowHeight(window)), GetGeometry().Item2);
            return new Point(bounds.Left, bounds.Top);
        }

        private void SaveDrag(McpToastWindow window, Point point)
        {
            if (!ReferenceEquals(window, _window))
                return;
            var options = _positionOptions();
            // Dragging collapses details; thumbnail/branding may still occupy space.
            _compactHeight = WindowHeight(window);
            _growUp = options.Bottom;
            var anchor = ToastPlacement.Anchor(GetGeometry().Item1, WindowWidth(window), _compactHeight,
                new ToastPositionOptions(options.Right, options.Bottom));
            _positionChanged?.Invoke(options.WithOffset(point.X - anchor.Left, point.Y - anchor.Top));
            PositionWindow(window);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

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
