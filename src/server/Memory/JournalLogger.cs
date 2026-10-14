using System;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin;

namespace RvtMcp.Server.Memory
{
    public class JournalLogger
    {
        private readonly string _journalDir;
        private readonly object _lock = new object();
        private string _currentDate;
        private string _currentPath;
        private readonly bool _enabled;

        public string JournalDir => _journalDir;

        public JournalLogger(bool enabled = false, string journalDir = null)
        {
            _enabled = enabled;
            _journalDir = journalDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "rvt-mcp", "journal");
        }

        public void Log(JournalEntry entry)
        {
            if (!_enabled) return;
            lock (_lock)
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                if (today != _currentDate)
                {
                    _currentDate = today;
                    _currentPath = Path.Combine(_journalDir, $"{today}.jsonl");
                }

                try
                {
                    Directory.CreateDirectory(_journalDir);
                    var line = JsonConvert.SerializeObject(entry, Formatting.None);
                    BoundedLogFile.Append(_currentPath, line, "journal");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("Journal", "Journal write failed: " + ex.Message);
                }
            }
        }

        public JournalEntry[] ReadDay(string date) => ReadDayEntries(date).ToArray();

        public IEnumerable<JournalEntry> ReadDayEntries(string date)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(date ?? "", @"^\d{4}-\d{2}-\d{2}$"))
                throw new ArgumentException("A journal date must use yyyy-MM-dd.", nameof(date));
            foreach (var file in BoundedLogFile.Files(_journalDir, "journal").Where(p => Path.GetFileName(p).StartsWith(date, StringComparison.Ordinal)))
                foreach (var line in BoundedLogFile.ReadLinesSafe(file))
                {
                    JournalEntry entry = null;
                    try { entry = JsonConvert.DeserializeObject<JournalEntry>(line); } catch { }
                    if (entry != null) yield return entry;
                }
        }

        public string[] ListDates() => BoundedLogFile.Files(_journalDir, "journal")
            .Select(p => Path.GetFileName(p).Substring(0, 10)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }
}
