using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RvtMcp.Plugin.Documents
{
    public static class BackupFileCounter
    {
        public static int Count(string modelPath)
        {
            var full = Path.GetFullPath(modelPath);
            var extension = Path.GetExtension(full);
            if (!string.Equals(extension, ".rvt", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".rfa", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".rte", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Model must be an .rvt, .rfa or .rte file.", nameof(modelPath));
            var pattern = "^" + Regex.Escape(Path.GetFileNameWithoutExtension(full)) + @"\.[0-9]{4}" + Regex.Escape(extension) + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Directory.EnumerateFiles(Path.GetDirectoryName(full)).Count(path => regex.IsMatch(Path.GetFileName(path)));
        }
    }
}
