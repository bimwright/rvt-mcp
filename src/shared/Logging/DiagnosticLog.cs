using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RvtMcp.Plugin
{
    /// <summary>Diagnostics are best effort and never perform disk IO on the Revit/UI caller.</summary>
    public static class DiagnosticLog
    {
        private sealed class Entry { public string Component, Message, Year, Root; public TaskCompletionSource<bool> Flush; public bool Cleanup; }
        private sealed class Repeat { public Entry Entry; public int Count; public long Started; }
        private static readonly BlockingCollection<Entry> Queue = new BlockingCollection<Entry>(256);
        private static readonly Thread Worker;
        private static readonly int Pid = Process.GetCurrentProcess().Id;
        private static long _dropped;
        internal static string LocalAppDataOverride { get; set; }
        public static long Dropped => Interlocked.Read(ref _dropped);
        public const long FileBytes = 10L * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        static DiagnosticLog()
        {
            Worker = new Thread(Run) { IsBackground = true, Name = "RvtMcp.Diagnostics" };
            Worker.Start();
        }

        public static void Write(string component, string message, string year = null)
        {
            var safe = SecretMasker.Mask(message ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
            if (safe.Length > 2048) safe = safe.Substring(0, 2048) + " [truncated]";
            var root = Path.Combine(LocalAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");
            if (!Queue.TryAdd(new Entry { Component = component, Message = safe, Year = year ?? "unknown", Root = root })) Interlocked.Increment(ref _dropped);
        }

        public static bool Flush(int milliseconds = 1000)
        {
            var signal = new TaskCompletionSource<bool>();
            if (!Queue.TryAdd(new Entry { Flush = signal })) return false;
            return signal.Task.Wait(milliseconds);
        }

        public static void MaintainLegacyLogs()
        {
            var root = Path.Combine(LocalAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");
            if (!Queue.TryAdd(new Entry { Cleanup = true, Root = root })) Interlocked.Increment(ref _dropped);
        }

        private static void Run()
        {
            var repeats = new Dictionary<string, Repeat>();
            while (true)
            {
                Entry entry;
                Queue.TryTake(out entry, 1000);
                if (entry?.Cleanup == true) { Cleanup(entry.Root); continue; }
                if (entry?.Flush != null)
                {
                    foreach (var repeat in repeats.Values) Summary(repeat);
                    repeats.Clear();
                    entry.Flush.TrySetResult(true);
                    continue;
                }
                var now = Stopwatch.GetTimestamp();
                foreach (var key in new List<string>(repeats.Keys))
                    if ((now - repeats[key].Started) / (double)Stopwatch.Frequency >= 30)
                    { Summary(repeats[key]); repeats.Remove(key); }
                if (entry == null) continue;
                var keyName = entry.Root + "|" + entry.Component + "|" + entry.Year + "|" + entry.Message;
                Repeat existing;
                if (repeats.TryGetValue(keyName, out existing)) { existing.Count++; continue; }
                if (repeats.Count >= 64) { var first = new List<string>(repeats.Keys)[0]; Summary(repeats[first]); repeats.Remove(first); }
                repeats[keyName] = new Repeat { Entry = entry, Started = now };
                Append(entry, entry.Message);
            }
        }

        private static void Summary(Repeat repeat)
        {
            if (repeat.Count > 0) Append(repeat.Entry, repeat.Entry.Message + " [repeated=" + repeat.Count + "]");
        }

        private static void Append(Entry entry, string message)
        {
            try
            {
                // Legacy tails and repeat summaries use this path too.
                message = SecretMasker.Mask(message ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
                if (message.Length > 2300) message = message.Substring(0, 2300) + " [truncated]";
                var root = entry.Root;
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "rvt-mcp.log");
                var text = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + " pid=" + Pid + " revit=" + entry.Year + " [" + entry.Component + "] " + message + "\n";
                var bytes = Utf8.GetBytes(text);
                BoundedLogFile.WithLock(path, () =>
                {
                    BoundedLogFile.RefuseLink(path);
                    var backup = path + ".1";
                    BoundedLogFile.RefuseLink(backup);
                    BoundedLogFile.CompactDiagnostics(path, FileBytes);
                    BoundedLogFile.CompactDiagnostics(backup, FileBytes);
                    if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > FileBytes)
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(path, backup);
                    }
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) stream.Write(bytes, 0, bytes.Length);
                    return true;
                }, 250);
            }
            catch { Interlocked.Increment(ref _dropped); } // No recursive log/fallback file.
        }

        private static void Cleanup(string current)
        {
            var local = Path.GetDirectoryName(Path.GetDirectoryName(current));
            foreach (var root in new[] { current, Path.Combine(local, "RvtMcp"), Path.Combine(local, "Bimwright") })
                foreach (var name in new[] { "revit-mcp.log", "debug.log", Path.Combine("baked", "compile-refs.log") })
                {
                    var old = Path.Combine(root, name);
                    try
                    {
                        if (!File.Exists(old)) continue;
                        BoundedLogFile.WithLock(old, () =>
                        {
                            BoundedLogFile.CompactDiagnostics(old, 64 * 1024);
                            var tail = string.Join(" | ", BoundedLogFile.ReadTail(old, 8));
                            var entry = new Entry { Component = "LegacyDiagnostics", Year = "unknown", Root = current };
                            Append(entry, "Retained tail from " + Path.GetFileName(old) + ": " + SecretMasker.Mask(tail));
                            // Preserve the bounded original tail as recovery data; do not delete an active legacy writer's file.
                            return true;
                        }, 250);
                    }
                    catch { Interlocked.Increment(ref _dropped); }
                }
            foreach (var group in new[] { "mcp-calls", "send-code-journal", "usage", "bake-audit", "journal" })
                try { BoundedLogFile.MaintainGroup(group == "journal" ? Path.Combine(current, "journal") : current, group); }
                catch { Interlocked.Increment(ref _dropped); }
        }
    }
}
