using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RvtMcp.Plugin
{
    /// <summary>Generated log files only. Append, rotation and retention share one process-wide file mutex.</summary>
    public static class BoundedLogFile
    {
        public const long RecordFileBytes = 5L * 1024 * 1024;
        public const long RecordGroupBytes = 100L * 1024 * 1024;
        public const int RecordRetentionDays = 30;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static T WithLock<T>(string path, Func<T> action, int timeoutMilliseconds = 2000)
        {
            var full = Path.GetFullPath(path);
            if (Path.DirectorySeparatorChar == '\\') full = full.ToUpperInvariant();
            using (var mutex = new Mutex(false, (Path.DirectorySeparatorChar == '\\' ? @"Global\" : "") + "RvtMcp.Log." + BakeRedactor.HashBody(full)))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(timeoutMilliseconds); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new TimeoutException("Timed out waiting for the MCP log file lock.");
                    return action();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        public static void Append(string path, string line, string group = null,
            long fileBytes = RecordFileBytes, long groupBytes = RecordGroupBytes,
            int retentionDays = RecordRetentionDays, DateTimeOffset? now = null)
        {
            path = Path.GetFullPath(path);
            group = group ?? Path.GetFileNameWithoutExtension(path);
            var directory = Path.GetDirectoryName(path);
            var lockPath = group == "journal" ? Path.Combine(directory, "journal.jsonl") : path;
            WithLock(lockPath, () => { AppendUnderLock(path, line, group, fileBytes, groupBytes, retentionDays, now); return true; });
        }

        public static void AppendUnderLock(string path, string line, string group,
            long fileBytes = RecordFileBytes, long groupBytes = RecordGroupBytes,
            int retentionDays = RecordRetentionDays, DateTimeOffset? now = null)
        {
            var bytes = Utf8.GetBytes((line ?? "") + "\n");
            if (bytes.LongLength > fileBytes) throw new InvalidDataException("Log record exceeds the file budget.");
            var utc = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
            var directory = Path.GetDirectoryName(path);
            RefuseLink(path);
            Directory.CreateDirectory(directory);
            if (File.Exists(path) && (new FileInfo(path).Length + bytes.LongLength > fileBytes || File.GetCreationTimeUtc(path).Date < utc.Date))
            {
                Rotate(path, utc); // A failed rotation never falls through to an oversized append.
            }
            Prune(directory, group, path, groupBytes - bytes.LongLength, utc.AddDays(-retentionDays));
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
        }

        private static void Rotate(string path, DateTime utc)
        {
            var archive = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-" + utc.ToString("yyyyMMdd-HHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            File.Move(path, archive);
        }

        /// <summary>Apply retention on startup even when an opt-in logger is currently disabled.</summary>
        public static void MaintainGroup(string directory, string group)
        {
            if (!Directory.Exists(directory)) return;
            var utc = DateTime.UtcNow;
            var active = Path.Combine(directory, group == "journal" ? utc.ToString("yyyy-MM-dd") + ".jsonl" : group + ".jsonl");
            var lockPath = group == "journal" ? Path.Combine(directory, "journal.jsonl") : active;
            WithLock(lockPath, () =>
            {
                RefuseLink(active);
                if (File.Exists(active) && (new FileInfo(active).Length > RecordFileBytes || File.GetLastWriteTimeUtc(active) < utc.AddDays(-RecordRetentionDays)))
                    Rotate(active, utc);
                Prune(directory, group, active, RecordGroupBytes, utc.AddDays(-RecordRetentionDays));
                return true;
            }, 250);
        }

        public static string[] Files(string directory, string group)
        {
            if (!Directory.Exists(directory)) return new string[0];
            var pattern = group == "journal"
                ? @"^\d{4}-\d{2}-\d{2}(?:-\d{8}-\d{6}(?:\d{7})?(?:-[0-9a-f]{32})?)?\.jsonl$"
                : "^" + Regex.Escape(group) + @"(?:-\d{8}-\d{6}(?:\d{7})?(?:-[0-9a-f]{32})?)?\.jsonl$";
            return Directory.GetFiles(directory, "*.jsonl").Where(p => Regex.IsMatch(Path.GetFileName(p), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(p => File.GetLastWriteTimeUtc(p)).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        }

        private static void Prune(string directory, string group, string active, long budget, DateTime cutoff)
        {
            var files = Files(directory, group);
            long total = files.Sum(p => new FileInfo(p).Length);
            foreach (var file in files)
            {
                if (string.Equals(file, active, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= cutoff && total <= budget) continue;
                var length = info.Length;
                // A locked archive prevents the new append; it never permits unbounded growth.
                File.Delete(file);
                total -= length;
            }
            if (total > budget) throw new IOException("Log group budget is unavailable.");
        }

        public static void RefuseLink(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Log maintenance refuses linked files.");
            for (var dir = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Log maintenance refuses linked directories.");
        }

        /// <summary>Bound allocation even for a malformed huge line; readers permit append/rotation.</summary>
        public static IEnumerable<string> ReadLines(string path, int maxLineCharacters = 5 * 1024 * 1024)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Utf8))
            {
                var line = new StringBuilder();
                bool overflow = false;
                int value;
                while ((value = reader.Read()) != -1)
                {
                    if (value == '\n')
                    {
                        if (!overflow) yield return line.ToString().TrimEnd('\r');
                        line.Clear(); overflow = false;
                    }
                    else if (!overflow)
                    {
                        if (line.Length >= maxLineCharacters) { line.Clear(); overflow = true; }
                        else line.Append((char)value);
                    }
                }
                if (!overflow && line.Length > 0) yield return line.ToString();
            }
        }

        public static string[] ReadTail(string path, int count)
        {
            var tail = new Queue<string>();
            foreach (var line in ReadLines(path))
            {
                tail.Enqueue(line);
                if (tail.Count > count) tail.Dequeue();
            }
            return tail.ToArray();
        }

        public static IEnumerable<string> ReadLinesSafe(string path)
        {
            using (var reader = ReadLines(path).GetEnumerator())
                while (true)
                {
                    bool next;
                    try { next = reader.MoveNext(); } catch (IOException) { yield break; } catch (UnauthorizedAccessException) { yield break; }
                    if (!next) yield break;
                    yield return reader.Current;
                }
        }

        /// <summary>Keep a recent UTF-8 tail of a known diagnostics file without copying its full size.</summary>
        public static void CompactDiagnostics(string path, long budget)
        {
            RefuseLink(path);
            if (!File.Exists(path) || new FileInfo(path).Length <= budget) return;
            var temporary = path + ".compact-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    input.Seek(Math.Max(0, input.Length - budget), SeekOrigin.Begin);
                    // Start after a newline, not in the middle of a UTF-8 character/record.
                    if (input.Position > 0) { int b; do { b = input.ReadByte(); } while (b != -1 && b != 10); }
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
                if (new FileInfo(temporary).Length > budget) throw new IOException("Diagnostic compaction exceeded its budget.");
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
