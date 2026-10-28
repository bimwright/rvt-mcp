using System;
using System.IO;
using System.Threading;

namespace RvtMcp.Plugin.Reports.Storage
{
    public sealed class ReportFolderWatcher : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<string> _log;
        private FileSystemWatcher _watcher;
        private Timer _timer;
        private bool _disposed;

        public event EventHandler Changed;

        public ReportFolderWatcher(string directory = null, Action<string> log = null)
        {
            _log = log;
            try
            {
                directory = directory ?? ReportStore.DefaultDirectory;
                Directory.CreateDirectory(directory);
                _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
                _watcher = new FileSystemWatcher(directory, "*.json");
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size;
                _watcher.Created += OnFileChanged;
                _watcher.Changed += OnFileChanged;
                _watcher.Deleted += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
                _watcher.Error += OnError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException
                || ex is ArgumentException || ex is NotSupportedException)
            {
                _watcher?.Dispose(); _watcher = null;
                _timer?.Dispose(); _timer = null;
                _log?.Invoke("Cannot start report watcher: " + ex.Message);
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs args)
        {
            ScheduleChange();
        }

        private void ScheduleChange()
        {
            lock (_gate) { if (!_disposed) _timer?.Change(300, Timeout.Infinite); }
        }

        private void OnTimer(object state)
        {
            EventHandler handler;
            lock (_gate) { if (_disposed) return; handler = Changed; }
            handler?.Invoke(this, EventArgs.Empty);
        }

        private void OnError(object sender, ErrorEventArgs args)
        {
            ScheduleChange();
            _log?.Invoke("Report watcher error: " + args.GetException().Message);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _watcher?.Dispose();
                _timer?.Dispose();
            }
        }
    }
}
