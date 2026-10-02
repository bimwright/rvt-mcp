using System;
using System.Threading.Tasks;
using RvtMcp.Plugin.Views.Update;

namespace RvtMcp.Plugin.Update
{
    /// <summary>
    /// Plugin glue for the update check: <see cref="Start"/> runs the (cached,
    /// at most daily) GitHub check on a thread-pool thread during OnStartup;
    /// <see cref="OnIdling"/> runs on Revit's UI thread and shows the modeless
    /// notice once per session after the main window exists.
    /// </summary>
    public sealed class UpdateNoticeCoordinator
    {
        private readonly Action<string> _log;
        private Task<UpdateInfo> _check;
        private bool _handled;
        private UpdateNoticeWindow _window;

        public UpdateNoticeCoordinator(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            if (UpdateChecker.IsDisabledByEnvironment())
            {
                _handled = true;
                _log("UpdateCheck: disabled by " + UpdateChecker.DisableEnvVar);
                return;
            }
            var current = UpdateChecker.VersionOf(typeof(UpdateNoticeCoordinator).Assembly);
            _check = Task.Run(() => UpdateChecker.CheckAsync(current));
        }

        public void OnIdling(IntPtr ownerHwnd)
        {
            if (_handled || _check == null || !_check.IsCompleted || ownerHwnd == IntPtr.Zero) return;
            _handled = true;
            try
            {
                var info = _check.Status == TaskStatus.RanToCompletion ? _check.Result : null;
                if (info == null || info.IsSkipped) return;
                _log("UpdateCheck: " + info.LatestVersion + " available (running " + info.CurrentVersion + ")");
                _window = new UpdateNoticeWindow(info);
                new System.Windows.Interop.WindowInteropHelper(_window).Owner = ownerHwnd;
                _window.Closed += (_, __) => _window = null;
                _window.Show();
            }
            catch (Exception ex)
            {
                _log("UpdateCheck: notice failed: " + ex.Message);
            }
        }

        public void Shutdown()
        {
            try { _window?.Close(); } catch { }
            _window = null;
        }
    }
}
