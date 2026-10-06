using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>One listener lifetime. Disposal and publication share the same gate.</summary>
    public sealed class DiscoveryPublisher : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _directory;
        private readonly string _year;
        private readonly int _pid;
        private readonly DateTime _start;
        private readonly Func<int, DateTime?, bool> _isAlive;
        private readonly Action<string> _restrictAcl;
        private readonly Action<string> _log;
        private readonly Func<string, string[]> _enumerateFiles;
        private bool _disposed;
        private string _token;

        public string TargetId => "revit-" + _year + "-" + _pid.ToString(CultureInfo.InvariantCulture);
        public string FileName => TargetId + ".json";

        public DiscoveryPublisher(string directory, string year, int pid, DateTime startUtc,
            Func<int, DateTime?, bool> isAlive = null, Action<string> restrictAcl = null, Action<string> log = null,
            Func<string, string[]> enumerateFiles = null)
        {
            _directory = directory;
            _year = year;
            _pid = pid;
            _start = startUtc.ToUniversalTime();
            _isAlive = isAlive ?? IsProcessAlive;
            _restrictAcl = restrictAcl;
            _log = log;
            _enumerateFiles = enumerateFiles ?? (pattern => Directory.GetFiles(_directory, pattern));
        }

        public void Publish(string transport, int? port, string pipeName, string token)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _token = token;
                var json = new JObject
                {
                    ["schema_version"] = 3,
                    ["host_app"] = "revit",
                    ["host_year"] = int.Parse(_year, CultureInfo.InvariantCulture),
                    ["revit_year"] = int.Parse(_year, CultureInfo.InvariantCulture),
                    ["pid"] = _pid,
                    ["process_start_utc"] = _start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    ["target_id"] = TargetId,
                    ["transport"] = transport,
                    ["port"] = port.HasValue ? new JValue(port.Value) : JValue.CreateNull(),
                    ["pipe_name"] = pipeName == null ? JValue.CreateNull() : new JValue(pipeName),
                    ["auth_token"] = token,
                    ["capabilities"] = new JArray("tool_catalog")
                }.ToString(Formatting.Indented);
                Write(FileName, json);
                Write("revit-" + _year + ".json", json);
            }
        }

        private void Write(string name, string json) =>
            AtomicDescriptorFile.TryWrite(Path.Combine(_directory, name), json, _pid, _restrictAcl, _log);

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                // A late Stop of an old listener must not delete a newer listener's token.
                var ownPath = Path.Combine(_directory, FileName);
                var own = TryRead(ownPath);
                if (own != null && own.Value<string>("auth_token") == _token)
                    TryDelete(ownPath);
                var legacyPath = Path.Combine(_directory, "revit-" + _year + ".json");
                var legacy = TryRead(legacyPath);
                if (legacy == null || legacy.Value<int?>("pid") != _pid || legacy.Value<string>("auth_token") != _token)
                    return;

                JObject replacement = null;
                DateTime? latest = null;
                int latestPid = 0;
                try
                {
                    foreach (var path in _enumerateFiles("revit-" + _year + "-*.json"))
                    {
                        var candidate = TryRead(path);
                        if (candidate == null) continue;
                        var pid = candidate.Value<int?>("pid") ?? 0;
                        var start = candidate.Value<DateTime?>("process_start_utc");
                        var id = "revit-" + _year + "-" + pid.ToString(CultureInfo.InvariantCulture);
                        if (pid <= 0 || pid == _pid || !string.Equals(Path.GetFileName(path), id + ".json", StringComparison.OrdinalIgnoreCase)
                            || candidate.Value<int?>("host_year") != int.Parse(_year, CultureInfo.InvariantCulture)
                            || !string.Equals(candidate.Value<string>("host_app"), "revit", StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrWhiteSpace(candidate.Value<string>("auth_token")) || !_isAlive(pid, start)) continue;
                        if (replacement == null || Nullable.Compare(start, latest) > 0 || (start == latest && pid > latestPid))
                        { replacement = candidate; latest = start; latestPid = pid; }
                    }
                }
                catch (Exception ex) { Log("Legacy handover scan failed: " + ex.Message); return; }
                if (replacement == null) TryDelete(legacyPath);
                else Write("revit-" + _year + ".json", replacement.ToString(Formatting.Indented));
            }
        }

        private void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log("Discovery cleanup failed: " + ex.Message); }
        }

        private void Log(string message) { try { _log?.Invoke(message); } catch { } }

        private static JObject TryRead(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) return JObject.Parse(reader.ReadToEnd());
            }
            catch { return null; }
        }

        private static bool IsProcessAlive(int pid, DateTime? expectedStart)
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(pid))
                {
                    if (process.HasExited) return false;
                    if (!expectedStart.HasValue) return true;
                    return Math.Abs((process.StartTime.ToUniversalTime() - expectedStart.Value.ToUniversalTime()).TotalSeconds) <= 1;
                }
            }
            catch (ArgumentException) { return false; }
            catch { return true; } // Lack of permission is not proof of death.
        }
    }

    public static class AtomicDescriptorFile
    {
        public static bool TryWrite(string path, string content, int pid, Action<string> restrictAcl = null,
            Action<string> log = null, Action<int> delay = null)
        {
            var temp = path + "." + pid.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(content);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                try { restrictAcl?.Invoke(temp); } catch (Exception ex) { SafeLog(log, "ACL restriction failed: " + ex.Message); }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        return true;
                    }
                    catch (IOException) when (attempt < 5) { (delay ?? Thread.Sleep)(50 << attempt); }
                    catch (UnauthorizedAccessException) when (attempt < 5) { (delay ?? Thread.Sleep)(50 << attempt); }
                }
            }
            catch (Exception ex) { SafeLog(log, "Discovery write failed: " + ex.Message); return false; }
            finally { try { File.Delete(temp); } catch { } }
        }

        private static void SafeLog(Action<string> log, string message) { try { log?.Invoke(message); } catch { } }
    }
}
