using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RvtMcp.Plugin.Documents
{
    public sealed class PruneCandidate
    {
        public string Id { get; set; }
        public string File { get; set; }
        public string FullPath { get; set; }
        public bool Exists { get; set; }
        public long Bytes { get; set; }
        public DateTime? LastWriteUtc { get; set; }
    }

    public sealed class CheckpointPrunePlan
    {
        public List<PruneCandidate> DeleteFiles { get; set; }
        public List<PruneCandidate> MissingEntries { get; set; }
        public long BytesToFree { get; set; }
        public string PlanHash { get; set; }
    }

    public static class CheckpointPlanner
    {
        public static CheckpointPrunePlan Plan(string directory, IEnumerable<CheckpointEntry> entries, int keepLast)
        {
            if (keepLast < 1 || keepLast > 100) throw new ArgumentOutOfRangeException(nameof(keepLast));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var root = Path.GetFullPath(directory);
            if (!Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .EndsWith(".checkpoints", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The directory is not a checkpoints directory.");
            try
            {
                var rootAttributes = File.GetAttributes(root);
                if ((rootAttributes & FileAttributes.Directory) == 0 || (rootAttributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Checkpoint directory is not a regular directory.");
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }

            var states = new List<EntryState>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id)
                    || string.IsNullOrWhiteSpace(entry.File) || !files.Add(entry.File)
                    || !DateTimeOffset.TryParse(entry.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
                    throw new InvalidDataException("Invalid checkpoint entry.");
                var path = ResolvePath(root, entry.File);
                var candidate = new PruneCandidate { Id = entry.Id, File = entry.File, FullPath = path };
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new InvalidDataException("Checkpoint is not a regular file.");
                    var info = new FileInfo(path);
                    candidate.Exists = true;
                    candidate.Bytes = info.Length;
                    candidate.LastWriteUtc = info.LastWriteTimeUtc;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                states.Add(new EntryState { Entry = entry, Created = created, Candidate = candidate });
            }

            var missing = states.Where(s => !s.Candidate.Exists).Select(s => s.Candidate).ToList();
            var deletions = states.Where(s => s.Candidate.Exists && !s.Entry.Milestone)
                .OrderByDescending(s => s.Created).ThenBy(s => s.Entry.Id, StringComparer.Ordinal)
                .Skip(keepLast).Select(s => s.Candidate).ToList();
            return new CheckpointPrunePlan
            {
                DeleteFiles = deletions,
                MissingEntries = missing,
                BytesToFree = deletions.Sum(c => c.Bytes),
                PlanHash = Hash(keepLast, deletions.Concat(missing))
            };
        }

        private static string ResolvePath(string root, string file)
        {
            if (Path.IsPathRooted(file) || file == "." || file == ".."
                || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || file.IndexOfAny(new[] { '/', '\\' }) >= 0)
                throw new InvalidDataException("Checkpoint file must be directly inside the checkpoints directory.");
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, file));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checkpoint file is outside the checkpoints directory.");
            return full;
        }

        private static string Hash(int keepLast, IEnumerable<PruneCandidate> candidates)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(keepLast);
                    foreach (var item in candidates.OrderBy(c => c.Id, StringComparer.Ordinal).ThenBy(c => c.File, StringComparer.Ordinal))
                    {
                        writer.Write(item.Id);
                        writer.Write(item.File);
                        writer.Write(item.Exists);
                        writer.Write(item.Bytes);
                        writer.Write(item.LastWriteUtc?.Ticks ?? 0);
                    }
                }
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        private sealed class EntryState
        {
            public CheckpointEntry Entry { get; set; }
            public DateTimeOffset Created { get; set; }
            public PruneCandidate Candidate { get; set; }
        }
    }
}
