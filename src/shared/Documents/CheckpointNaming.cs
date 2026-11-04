using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RvtMcp.Plugin.Documents
{
    public sealed class CheckpointName
    {
        public string Id { get; set; }
        public string File { get; set; }
    }

    public static class CheckpointNaming
    {
        // The caller must still use File.Copy(..., overwrite: false) to reject a concurrent collision.
        public static CheckpointName Next(string directory, string extension, DateTimeOffset localTime, IEnumerable<CheckpointEntry> entries)
        {
            if (!string.Equals(extension, ".rvt", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".rfa", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".rte", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Checkpoint extension must be .rvt, .rfa or .rte.", nameof(extension));
            var existingIds = new HashSet<string>((entries ?? Enumerable.Empty<CheckpointEntry>()).Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var existingFiles = new HashSet<string>((entries ?? Enumerable.Empty<CheckpointEntry>()).Select(e => e.File), StringComparer.OrdinalIgnoreCase);
            var stem = localTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            extension = extension.ToLowerInvariant();
            for (var suffix = 1; ; suffix++)
            {
                var id = suffix == 1 ? stem : stem + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                var file = id + extension;
                if (!existingIds.Contains(id) && !existingFiles.Contains(file) && !File.Exists(Path.Combine(directory, file)))
                    return new CheckpointName { Id = id, File = file };
            }
        }
    }
}
