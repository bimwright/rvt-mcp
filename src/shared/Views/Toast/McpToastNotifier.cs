using System;
using System.Runtime.InteropServices;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// Adapts normalized MCP outcomes and lifecycle events to the pure activity
    /// state machine. The notifier never constructs a window and never owns a
    /// queue: ActivityAggregator is the single source of truth for the card.
    /// </summary>
    public sealed class McpToastNotifier
    {
        private readonly McpToastHost _host;
        private readonly ActivityAggregator _activity;
        private readonly Func<bool> _isEnabled;
        private IntPtr _ownerHwnd;

        public McpToastNotifier(McpToastHost host, Func<bool> isEnabled)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _activity = host.Aggregator;
            _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
            _host.SetFrameUsableProvider(IsOwnerFrameUsable);
            _host.SetActivityClickHandler(_ =>
            {
                try { App.Instance?.ShowOrFocusHistoryWindowFromToast(); }
                catch (Exception ex) { App.DebugLog("Toast history open failed: " + ex.Message); }
            });
        }

        public void SetOwnerHandle(IntPtr hwnd)
        {
            _ownerHwnd = hwnd;
            _host.SetOwnerHandle(hwnd);
        }

        public void SetHostDispatcher(System.Windows.Threading.Dispatcher dispatcher) =>
            _host.SetHostDispatcher(dispatcher);

        public void OnCompleted(
            string toolName,
            string paramsJson,
            string resultJson,
            bool success,
            string errorMessage,
            long durationMs,
            string toolDescription)
        {
            if (!_isEnabled())
                return;

            var vm = ToastContentBuilder.BuildCompleted(
                toolName,
                paramsJson,
                resultJson,
                success,
                errorMessage,
                durationMs,
                toolDescription);

            // A capture contributes to the image count, but the activity card never
            // embeds or opens the image. The path has already passed the builder's
            // local safety policy.
            Record(vm.Title, vm.Body, vm.Success, vm.Success && !string.IsNullOrEmpty(vm.ThumbnailPath));
        }

        /// <summary>One-shot connection confirmation, independent of activity counters.</summary>
        public void OnClientConnected(string connectionInfo)
        {
            if (!_isEnabled())
                return;

            ShowStatus(
                L.T("toast.connected.title"),
                string.IsNullOrWhiteSpace(connectionInfo)
                    ? L.T("toast.connected.summary")
                    : L.T("toast.connected.summary") + " · " + connectionInfo,
                6);
        }

        /// <summary>
        /// Shared Ribbon/Settings transition. Turning off clears activity first and
        /// then permits exactly one status card explaining the new state.
        /// </summary>
        public void OnToastEnabledChanged(bool enabled, bool persisted = true)
        {
            var resetRequestedRender = _activity.Reset();
            if (enabled)
            {
                ShowStatus(StatusText("toast.status.enabled", "Toast notifications enabled"),
                    StatusSummary("toast.status.enabled.summary", "New activity will appear here.", persisted), 3,
                    allowWhenDisabled: true);
            }
            else
            {
                ShowStatus(StatusText("toast.status.disabled", "Toast notifications disabled"),
                    StatusSummary("toast.status.disabled.summary", "New activity is hidden until toast notifications are enabled.", persisted), 3,
                    allowWhenDisabled: true);
            }
            // Reset may have claimed the coalescing slot before ShowStatus ran. A
            // single posted render still reads the post-toggle state via TakeRender.
            if (resetRequestedRender)
                _host.Post(manager => manager.Render());
        }

        /// <summary>
        /// Idling bridge for results parked while Revit is minimized or modal. The
        /// manager owns its timer; this method only flushes the pending phase.
        /// </summary>
        public void FlushPendingIfUsable()
        {
            if (_activity.FlushIfUsable(IsOwnerFrameUsable()))
                _host.Post(manager => manager.Render());
        }

        public void Shutdown()
        {
            _activity.Reset();
            _host.Shutdown();
        }

        private void Record(string title, string body, bool success, bool hasImage)
        {
            if (_activity.RecordResult(title, body, success, hasImage, IsOwnerFrameUsable()))
                _host.Post(manager => manager.Render());
        }

        private void ShowStatus(string title, string body, int seconds, bool allowWhenDisabled = false)
        {
            if (!allowWhenDisabled && !_isEnabled())
                return;
            if (_activity.ShowStatus(title, body, seconds))
                _host.Post(manager => manager.Render());
        }

        private static string StatusText(string key, string fallback)
        {
            var value = L.T(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
                ? fallback
                : value;
        }

        private static string StatusSummary(string key, string fallback, bool persisted)
        {
            var summary = StatusText(key, fallback);
            if (persisted)
                return summary;

            var warning = StatusText(
                "toast.status.saveFailed",
                "Preference could not be saved; this session is still using the new state.");
            return summary + " · " + warning;
        }

        private bool IsOwnerFrameUsable()
        {
            var hwnd = _ownerHwnd;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                return true;
            return IsWindowVisible(hwnd) && !IsIconic(hwnd) && IsWindowEnabled(hwnd);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);
    }
}
