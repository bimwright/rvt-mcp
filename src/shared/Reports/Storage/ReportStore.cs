using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Storage
{
    public sealed class ReportSaveResult
    {
        public string ReportId { get; set; }
        public bool Replaced { get; set; }
    }

    public sealed class ReportStore
    {
        private readonly string _directory;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly Dictionary<string, SavedReport> _tasks = new Dictionary<string, SavedReport>(StringComparer.Ordinal);
        private readonly HashSet<string> _loggedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp", "reports");

        public ReportStore(string directory = null, Action<string> log = null)
        {
            _directory = Path.GetFullPath(directory ?? DefaultDirectory);
            _log = log;
        }

        public ReportSaveResult Save(AgentReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrEmpty(report.TaskId)) throw new ArgumentException("Task ID is required.", nameof(report));
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var now = DateTime.UtcNow;
                var replaced = _tasks.TryGetValue(report.TaskId, out var saved);
                if (!replaced)
                {
                    var id = Guid.NewGuid().ToString("N");
                    saved = new SavedReport { Id = id, CreatedUtc = ReportJson.FormatUtc(now),
                        Path = Path.Combine(_directory, now.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + id + ".json") };
                }
                report.ReportId = saved.Id;
                report.CreatedUtc = saved.CreatedUtc;
                report.UpdatedUtc = ReportJson.FormatUtc(now);
                var temp = saved.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var bytes = new UTF8Encoding(false).GetBytes(ReportJson.Serialize(report));
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(saved.Path)) File.Replace(temp, saved.Path, null);
                    else File.Move(temp, saved.Path);
                    _tasks[report.TaskId] = saved;
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                return new ReportSaveResult { ReportId = saved.Id, Replaced = replaced };
            }
        }

        public List<AgentReport> LoadNewest(int max = 500)
        {
            var reports = new List<AgentReport>();
            string[] paths;
            lock (_gate)
            {
                if (!Directory.Exists(_directory) || max <= 0) return reports;
                paths = Directory.GetFiles(_directory, "*.json")
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .Take(Math.Min(max, 500)).ToArray();
            }

            foreach (var path in paths)
            {
                try
                {
                    string json;
                    lock (_gate)
                    {
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(stream, Encoding.UTF8)) json = reader.ReadToEnd();
                    }
                    if (ReportJson.TryDeserialize(json, out var report, out var status)) reports.Add(report);
                    else LogOnce(path, "Cannot load report (" + status + "): " + path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    LogOnce(path, "Cannot load report: " + path + ": " + ex.Message);
                }
            }
            return reports;
        }

        public void Delete(string reportId)
        {
            if (!Guid.TryParseExact(reportId, "N", out var ignored)) return;
            lock (_gate)
            {
                if (!Directory.Exists(_directory)) return;
                foreach (var path in Directory.GetFiles(_directory, "*-" + reportId + ".json")) File.Delete(path);
            }
        }

        private void LogOnce(string path, string message)
        {
            lock (_gate)
            {
                if (_loggedFiles.Add(path)) _log?.Invoke(message);
            }
        }

        private sealed class SavedReport
        {
            public string Id;
            public string CreatedUtc;
            public string Path;
        }
    }
}
