using System;
using System.IO;
using Newtonsoft.Json;

namespace Bimwright.Server.Memory
{
    public class JournalLogger
    {
        private readonly string _journalDir;
        private readonly object _lock = new object();
        private string _currentDate;
        private string _currentPath;

        public string JournalDir => _journalDir;

        public JournalLogger()
        {
            _journalDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "journal");
            Directory.CreateDirectory(_journalDir);
        }

        public void Log(JournalEntry entry)
        {
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
                    var line = JsonConvert.SerializeObject(entry, Formatting.None);
                    File.AppendAllText(_currentPath, line + "\n");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Bimwright] Journal write failed: {ex.Message}");
                }
            }
        }

        public JournalEntry[] ReadDay(string date)
        {
            lock (_lock)
            {
                var path = Path.Combine(_journalDir, $"{date}.jsonl");
                if (!File.Exists(path)) return Array.Empty<JournalEntry>();

                var lines = File.ReadAllLines(path);
                var entries = new System.Collections.Generic.List<JournalEntry>(lines.Length);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        entries.Add(JsonConvert.DeserializeObject<JournalEntry>(line));
                    }
                    catch { }
                }
                return entries.ToArray();
            }
        }

        public string[] ListDates()
        {
            lock (_lock)
            {
                var files = Directory.GetFiles(_journalDir, "*.jsonl");
                var dates = new string[files.Length];
                for (int i = 0; i < files.Length; i++)
                    dates[i] = Path.GetFileNameWithoutExtension(files[i]);
                Array.Sort(dates);
                return dates;
            }
        }
    }
}
