using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin.Reports.Views
{
    /// <summary>Screen geometry for placing the Reports window, in DIPs.</summary>
    public interface IReportsScreen
    {
        /// <summary>Bounds of the window's owner (Revit), or null when the window has no live owner.</summary>
        ToastBounds? GetOwnerBounds(Window window);

        /// <summary>Work area of the monitor nearest <paramref name="near"/>; the window's monitor when null.</summary>
        ToastBounds GetWorkArea(Window window, ToastBounds? near);
    }

    /// <summary>
    /// Win32 geometry: owner rectangle and monitor work area converted px→DIP with the owner's DPI,
    /// the same conversion the activity card uses. A removed monitor falls back to the nearest one.
    /// </summary>
    public sealed class ReportsScreen : IReportsScreen
    {
        private const uint MonitorDefaultToNearest = 2;
        private const int DwmWindowCornerPreference = 33, DwmCornerRoundSmall = 3;

        public ToastBounds? GetOwnerBounds(Window window)
        {
            var owner = OwnerHandle(window);
            if (owner == IntPtr.Zero || !GetWindowRect(owner, out var rect)) return null;
            var scale = Scale(owner);
            return new ToastBounds(rect.Left * scale, rect.Top * scale, (rect.Right - rect.Left) * scale, (rect.Bottom - rect.Top) * scale);
        }

        public ToastBounds GetWorkArea(Window window, ToastBounds? near)
        {
            var reference = OwnerHandle(window);
            if (reference == IntPtr.Zero && window != null) reference = new WindowInteropHelper(window).Handle;
            var scale = Scale(reference);
            var monitor = IntPtr.Zero;
            if (near.HasValue && Finite(near.Value))
            {
                var px = new RECT
                {
                    Left = (int)Math.Round(near.Value.Left / scale), Top = (int)Math.Round(near.Value.Top / scale),
                    Right = (int)Math.Round(near.Value.Right / scale), Bottom = (int)Math.Round(near.Value.Bottom / scale)
                };
                monitor = MonitorFromRect(ref px, MonitorDefaultToNearest);
            }
            else if (reference != IntPtr.Zero) monitor = MonitorFromWindow(reference, MonitorDefaultToNearest);
            var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                return new ToastBounds(info.Work.Left * scale, info.Work.Top * scale,
                    (info.Work.Right - info.Work.Left) * scale, (info.Work.Bottom - info.Work.Top) * scale);
            var area = SystemParameters.WorkArea;
            return new ToastBounds(area.Left, area.Top, area.Width, area.Height);
        }

        /// <summary>
        /// How far a maximized window extends past its monitor's work area on each side, in the window's DIPs.
        /// Falls back to the system resize border when the window rectangle cannot be read.
        /// </summary>
        internal static Thickness MaximizedOverhang(Window window)
        {
            var fallback = SystemParameters.WindowResizeBorderThickness;
            var hwnd = window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
            var source = window == null ? null : PresentationSource.FromVisual(window);
            if (hwnd == IntPtr.Zero || source?.CompositionTarget == null || !GetWindowRect(hwnd, out var rect)) return fallback;
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return fallback;
            var toDip = source.CompositionTarget.TransformFromDevice;
            return new Thickness(Math.Max(0, info.Work.Left - rect.Left) * toDip.M11, Math.Max(0, info.Work.Top - rect.Top) * toDip.M22,
                Math.Max(0, rect.Right - info.Work.Right) * toDip.M11, Math.Max(0, rect.Bottom - info.Work.Bottom) * toDip.M22);
        }

        /// <summary>
        /// Asks Windows 11 for small (4 px) rounded corners; a WindowChrome window without a glass frame stays square otherwise.
        /// Earlier Windows reject the attribute and keep square corners. A maximized window is never rounded.
        /// </summary>
        internal static void RoundCorners(Window window)
        {
            var hwnd = window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var preference = DwmCornerRoundSmall;
            try { DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static IntPtr OwnerHandle(Window window)
        {
            if (window == null) return IntPtr.Zero;
            var owner = new WindowInteropHelper(window).Owner;
            return owner != IntPtr.Zero && IsWindow(owner) ? owner : IntPtr.Zero;
        }

        /// <summary>DIPs per pixel for <paramref name="hwnd"/>; 1 when the DPI is unknown.</summary>
        private static double Scale(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 1.0;
            try
            {
                var dpi = GetDpiForWindow(hwnd);
                return dpi > 0 ? 96.0 / dpi : 1.0;
            }
            catch (EntryPointNotFoundException) { return 1.0; } // Windows before 10 1607
            catch { return 1.0; }
        }

        private static bool Finite(ToastBounds b) =>
            !double.IsNaN(b.Left) && !double.IsInfinity(b.Left) && !double.IsNaN(b.Top) && !double.IsInfinity(b.Top)
            && !double.IsNaN(b.Width) && !double.IsInfinity(b.Width) && !double.IsNaN(b.Height) && !double.IsInfinity(b.Height);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
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
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
