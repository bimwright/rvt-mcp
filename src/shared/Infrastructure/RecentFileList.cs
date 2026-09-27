using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RvtMcp.Plugin
{
    public sealed class RecentFileEntry
    {
        public int Index { get; set; }
        public string Path { get; set; }
    }

    /// <summary>
    /// Revit writes the start-page MRU to Revit.ini under [Recent File List]
    /// as File1=..., File2=..., with gaps allowed. File1 is the most recent.
    /// </summary>
    public static class RecentFileList
    {
        public const int MaxEntries = 50;
        public const string Section = "[Recent File List]";

        public static string IniPath(string revitYear)
        {
            if (string.IsNullOrWhiteSpace(revitYear) || revitYear.Length != 4
                || revitYear.Any(c => c < '0' || c > '9'))
                throw new ArgumentException("Revit year must be a 4-digit calendar year.", nameof(revitYear));

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Autodesk Revit " + revitYear, "Revit.ini");
        }

        public static IReadOnlyList<RecentFileEntry> Parse(string iniText)
        {
            var found = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(iniText))
                return Array.Empty<RecentFileEntry>();

            var inSection = false;
            using (var reader = new StringReader(iniText))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == ';')
                        continue;
                    if (trimmed[0] == '[')
                    {
                        inSection = string.Equals(trimmed, Section, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection)
                        continue;

                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = trimmed.Substring(0, eq).Trim();
                    var value = trimmed.Substring(eq + 1).Trim();
                    if (value.Length == 0)
                        continue;
                    if (key.Length <= 4 || !key.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int index;
                    if (!int.TryParse(key.Substring(4), out index) || index <= 0)
                        continue;
                    found[index] = value;
                }
            }

            return found
                .OrderBy(pair => pair.Key)
                .Take(MaxEntries)
                .Select(pair => new RecentFileEntry { Index = pair.Key, Path = pair.Value })
                .ToList();
        }
    }
}
