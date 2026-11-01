using System;
using System.IO;

namespace RvtMcp.Plugin.Reports.Storage
{
    public static class ReportRetention
    {
        public static int Cleanup(string directory, TimeSpan retention, DateTime utcNow, Action<string> log)
        {
            var deleted = 0;
            var cutoff = utcNow.ToUniversalTime().Subtract(retention);
            try
            {
                if (!Directory.Exists(directory)) return 0;
                foreach (var path in Directory.GetFiles(directory))
                {
                    var extension = Path.GetExtension(path);
                    if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                        {
                            File.Delete(path);
                            deleted++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                    {
                        log?.Invoke("Cannot clean report file " + path + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                log?.Invoke("Cannot clean report directory " + directory + ": " + ex.Message);
            }
            return deleted;
        }
    }
}
