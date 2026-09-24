using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RvtMcp.Plugin.Views.Toast
{
    public sealed class McpToastNotifier
    {
        private const int MaxPending = 5;
        private readonly McpToastHost _host;
        private readonly Func<bool> _isEnabled;
        private readonly object _pendingGate = new object();
        private readonly List<McpToastViewModel> _pending = new List<McpToastViewModel>();
        private IntPtr _ownerHwnd;

        public McpToastNotifier(McpToastHost host, Func<bool> isEnabled)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
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

            Deliver(vm);
        }

        /// <summary>
        /// One-shot "agent connected" confirmation when a client first attaches
        /// to the transport (or re-attaches after a drop). Not a tool result.
        /// </summary>
        public void OnClientConnected(string connectionInfo)
        {
            if (!_isEnabled())
                return;

            var vm = new McpToastViewModel
            {
                CommandName = "client_connected",
                Title = "Agent connected",
                CategoryLabel = "MCP · Connected",
                Summary = "rvt-mcp is ready",
                Detail = connectionInfo,
                Kind = ToolActivityKind.Read,
                Success = true,
                AutoDismissSeconds = 6
            };

            // Startup confirmation posts directly, not via Deliver: during boot the
            // frame can be disabled by the home/splash screen, which would hold this
            // one-shot toast past the moment the user looks for it.
            _host.Post(manager => manager.Complete(vm));
        }

        /// <summary>
        /// Show now when the Revit frame is usable, else hold the toast until the
        /// frame is restored. A minimized or modal-blocked frame still gets its
        /// toasts — they flush on the next usable tick instead of firing unseen
        /// or covering a dialog's buttons.
        /// </summary>
        private void Deliver(McpToastViewModel vm)
        {
            if (IsOwnerFrameUsable())
            {
                _host.Post(manager => manager.Complete(vm));
                return;
            }

            lock (_pendingGate)
            {
                _pending.Add(vm);
                while (_pending.Count > MaxPending)
                    _pending.RemoveAt(0);
            }
        }

        /// <summary>
        /// Flush held toasts once the Revit frame is usable again. Called on the
        /// Revit UI thread by IdlingUpdater — cheap early-out when nothing is held.
        /// </summary>
        public void FlushPendingIfUsable()
        {
            List<McpToastViewModel> held;
            lock (_pendingGate)
            {
                if (_pending.Count == 0 || !IsOwnerFrameUsable())
                    return;
                held = new List<McpToastViewModel>(_pending);
                _pending.Clear();
            }

            foreach (var vm in held)
                _host.Post(manager => manager.Complete(vm));
        }

        /// <summary>No known frame → keep showing toasts (positions at screen edge).</summary>
        private bool IsOwnerFrameUsable()
        {
            var hwnd = _ownerHwnd;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                return true;
            return IsWindowVisible(hwnd) && !IsIconic(hwnd) && IsWindowEnabled(hwnd);
        }

        public void DismissAll()
        {
            lock (_pendingGate)
                _pending.Clear();
            _host.DismissAll(synchronous: false);
        }

        public void Shutdown()
        {
            lock (_pendingGate)
                _pending.Clear();
            _host.Shutdown();
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
