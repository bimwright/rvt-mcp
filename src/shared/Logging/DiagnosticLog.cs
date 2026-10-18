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
        private sealed class CleanupItem { public string Path, Name, Group; }
        private sealed class CleanupState
        {
            public string Root;
            public List<CleanupItem> Items;
            public int Attempt, Completed, Skipped, Exhausted;
            public long RetryAfter;
        }
        private static readonly BlockingCollection<Entry> Queue = new BlockingCollection<Entry>(256);
        private static readonly ConcurrentDictionary<string, byte> CleanupRoots = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private static readonly Thread Worker;
        private static readonly int Pid = Process.GetCurrentProcess().Id;
        private static long _dropped;
        internal static string LocalAppDataOverride { get; set; }
        internal static Func<long> LegacyCleanupTimestamp { get; set; } = Stopwatch.GetTimestamp;
        private const int CleanupAttempts = 3;
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
            // One bounded campaign per profile/process, including after retries are exhausted.
            if (!CleanupRoots.TryAdd(root, 0)) return;
            if (!Queue.TryAdd(new Entry { Cleanup = true, Root = root }))
            {
                byte ignored;
                CleanupRoots.TryRemove(root, out ignored);
                Interlocked.Increment(ref _dropped);
            }
        }

        private static void Run()
        {
            var repeats = new Dictionary<string, Repeat>();
            var cleanups = new List<CleanupState>();
            while (true)
            {
                Entry entry;
                Queue.TryTake(out entry, 1000);
                // Retry on this worker rather than queuing timers that can multiply or be dropped.
                foreach (var cleanup in new List<CleanupState>(cleanups))
                    if (LegacyCleanupTimestamp() >= cleanup.RetryAfter && !Cleanup(cleanup)) cleanups.Remove(cleanup);
                if (entry?.Cleanup == true)
                {
                    var cleanup = CreateCleanup(entry.Root);
                    if (Cleanup(cleanup)) cleanups.Add(cleanup);
                    continue;
                }
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

        private static CleanupState CreateCleanup(string current)
        {
            var state = new CleanupState { Root = current, Items = new List<CleanupItem>() };
            var local = Path.GetDirectoryName(Path.GetDirectoryName(current));
            foreach (var root in new[] { current, Path.Combine(local, "RvtMcp"), Path.Combine(local, "Bimwright") })
                foreach (var name in new[] { "revit-mcp.log", "debug.log", Path.Combine("baked", "compile-refs.log") })
                    state.Items.Add(new CleanupItem { Path = Path.Combine(root, name), Name = (root == current ? "current" : Path.GetFileName(root)) + "/" + name.Replace('\\', '/') });
            foreach (var group in new[] { "mcp-calls", "send-code-journal", "usage", "bake-audit", "journal" })
                state.Items.Add(new CleanupItem { Path = group == "journal" ? Path.Combine(current, "journal") : current, Name = "current/" + group, Group = group });
            return state;
        }

        private static bool Cleanup(CleanupState state)
        {
            state.Attempt++;
            var pending = new List<CleanupItem>();
            foreach (var item in state.Items)
            {
                try
                {
                    if (item.Group != null) BoundedLogFile.MaintainGroup(item.Path, item.Group);
                    else
                    {
                        BoundedLogFile.RefuseLink(item.Path);
                        if (File.Exists(item.Path))
                            BoundedLogFile.WithLock(item.Path, () =>
                            {
                                BoundedLogFile.CompactDiagnostics(item.Path, 64 * 1024);
                                var tail = string.Join(" | ", BoundedLogFile.ReadTail(item.Path, 8));
                                Append(CleanupEntry(state.Root), "Retained tail from " + item.Name + ": " + SecretMasker.Mask(tail));
                                // Preserve the bounded original tail; an old process may still own the file.
                                return true;
                            }, 250);
                    }
                    state.Completed++;
                }
                catch (BoundedLogFile.UnsafeLogPathException) { CleanupFailure(state, item, "unsafe_link", false, pending); }
                catch (UnauthorizedAccessException) { CleanupFailure(state, item, "access_denied", false, pending); }
                catch (System.Security.SecurityException) { CleanupFailure(state, item, "access_denied", false, pending); }
                catch (TimeoutException) { CleanupFailure(state, item, "lock_timeout", true, pending); }
                catch (IOException) { CleanupFailure(state, item, "io_busy_or_unavailable", true, pending); }
                catch { CleanupFailure(state, item, "maintenance_failed", false, pending); }
            }
            state.Items = pending;
            var status = pending.Count > 0 ? "pending" : state.Exhausted > 0 ? "exhausted" : state.Skipped > 0 ? "completed_with_skips" : "complete";
            Append(CleanupEntry(state.Root), "Legacy cleanup status=" + status + " attempt=" + state.Attempt + "/" + CleanupAttempts +
                " completed=" + state.Completed + " pending=" + pending.Count + " skipped=" + state.Skipped + " exhausted=" + state.Exhausted);
            if (pending.Count == 0) return false;
            state.RetryAfter = LegacyCleanupTimestamp() + (state.Attempt == 1 ? 5L : 30L) * Stopwatch.Frequency;
            return true;
        }

        private static Entry CleanupEntry(string root) => new Entry { Component = "LegacyDiagnostics", Year = "unknown", Root = root };

        private static void CleanupFailure(CleanupState state, CleanupItem item, string reason, bool retry, List<CleanupItem> pending)
        {
            Interlocked.Increment(ref _dropped);
            string status;
            if (retry && state.Attempt < CleanupAttempts) { pending.Add(item); status = "pending"; }
            else if (retry) { state.Exhausted++; status = "exhausted"; }
            else { state.Skipped++; status = "skipped"; }
            Append(CleanupEntry(state.Root), "Legacy cleanup item=" + item.Name + " status=" + status + " attempt=" + state.Attempt + "/" + CleanupAttempts + " reason=" + reason +
                (status == "pending" ? " retry_in_seconds=" + (state.Attempt == 1 ? 5 : 30) : ""));
        }
    }
}
