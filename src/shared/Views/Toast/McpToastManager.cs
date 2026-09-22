using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RvtMcp.Plugin.Views.Toast
{
    internal sealed class McpToastManager
    {
        private const int MaxToasts = 4;
        private const double Gap = 8;
        private const double EdgeMargin = 16;
        private readonly Dispatcher _dispatcher;
        private readonly List<McpToastWindow> _active = new List<McpToastWindow>();
        private IntPtr _ownerHandle;

        public McpToastManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void SetOwnerHandle(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
                _ownerHandle = hwnd;
        }

        public void Complete(McpToastViewModel vm)
        {
            EnsureDispatcher();

            EnforceCapBeforeAdd();
            var window = new McpToastWindow(vm, OnToastClosed);
            AttachOwner(window);
            _active.Insert(0, window);
            // Show may pump close callbacks that animate the active stack.
            // Give the new window finite coordinates before that can happen.
            ReflowAll(animate: false);
            window.Show();
            window.PlayEnterAnimation();
            window.StartAutoDismiss();
            ReflowAll(animate: false);
        }

        public void DismissAllImmediate()
        {
            EnsureDispatcher();

            var toasts = _active.ToList();
            _active.Clear();
            foreach (var toast in toasts)
                toast.CloseImmediate();
        }

        private void EnforceCapBeforeAdd()
        {
            while (_active.Count >= MaxToasts)
            {
                var oldest = _active[_active.Count - 1];
                _active.RemoveAt(_active.Count - 1);
                oldest.CloseImmediate();
            }
        }

        private void OnToastClosed(McpToastWindow toast)
        {
            if (_active.Remove(toast))
                ReflowAll(animate: true);
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

        private void ReflowAll(bool animate)
        {
            var owner = GetValidOwnerHandle();
            double startTop = EdgeMargin;
            double startLeft = EdgeMargin;

            if (owner != IntPtr.Zero && GetWindowRect(owner, out var rect))
            {
                GetOwnerDpiScale(owner, out var dpiX, out var dpiY);
                startLeft = rect.Left * dpiX + EdgeMargin;
                startTop = rect.Top * dpiY + EdgeMargin;
            }

            var currentTop = startTop;
            foreach (var toast in _active)
            {
                var height = toast.ActualHeight > 0 ? toast.ActualHeight : 72;

                if (animate)
                    toast.AnimateToPosition(currentTop, startLeft);
                else
                    toast.SetPosition(currentTop, startLeft);

                currentTop += height + Gap;
            }
        }

        private IntPtr GetValidOwnerHandle()
        {
            if (_ownerHandle != IntPtr.Zero && IsWindow(_ownerHandle))
                return _ownerHandle;

            // Prefer keeping last known good owner over Process.MainWindowHandle
            // (which can point at a splash/dialog). Clear only when truly invalid.
            if (_ownerHandle != IntPtr.Zero && !IsWindow(_ownerHandle))
                _ownerHandle = IntPtr.Zero;

            return _ownerHandle;
        }

        private void GetOwnerDpiScale(IntPtr hwnd, out double dpiX, out double dpiY)
        {
            // Default: 96 DPI → 1 DIP per physical pixel.
            dpiX = 1.0;
            dpiY = 1.0;

            try
            {
                // Windows 10 1607+: per-monitor DPI for the owner HWND.
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    dpiX = 96.0 / dpi;
                    dpiY = dpiX;
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Older OS — fall through to PresentationSource / default.
            }
            catch
            {
                // Ignore and fall through.
            }

            var first = _active.FirstOrDefault();
            if (first == null)
                return;

            var source = PresentationSource.FromVisual(first);
            if (source?.CompositionTarget != null)
            {
                var transform = source.CompositionTarget.TransformFromDevice;
                dpiX = transform.M11;
                dpiY = transform.M22;
            }
        }

        private void EnsureDispatcher()
        {
            if (!_dispatcher.CheckAccess())
                throw new InvalidOperationException("McpToastManager must run on the toast dispatcher thread.");
        }

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
