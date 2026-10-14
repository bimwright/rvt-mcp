using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RvtMcp.Plugin
{
    internal static class DiscoveryCoordination
    {
        internal static string Name(string directory, string year)
        {
            var path = Path.GetFullPath(Path.Combine(directory, "revit-" + year + ".json"));
            var windows = Path.DirectorySeparatorChar == '\\';
            if (windows) path = path.ToUpperInvariant();
            using (var sha = SHA256.Create())
                return (windows ? @"Global\" : "") + "RvtMcp.Discovery." +
                    BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "");
        }

        internal static void Run(string name, Action action, int timeoutMilliseconds = 2000, Action<string> log = null)
        {
            Mutex mutex;
            try { mutex = new Mutex(false, name); }
            catch (Exception ex) { log?.Invoke("Discovery coordination failed: " + ex.Message); return; }
            using (mutex)
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(timeoutMilliseconds); }
                    catch (AbandonedMutexException) { acquired = true; }
                    catch (Exception ex) { log?.Invoke("Discovery coordination failed: " + ex.Message); return; }
                    if (!acquired) { log?.Invoke("Discovery coordination timed out; descriptors were left unchanged."); return; }
                    action();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
    }
}
