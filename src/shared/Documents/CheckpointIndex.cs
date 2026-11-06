using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace RvtMcp.Plugin.Documents
{
    public sealed class CheckpointSource
    {
        [JsonProperty("number_of_saves", Required = Required.Always)] public int NumberOfSaves { get; set; }
        [JsonProperty("version_guid", Required = Required.Always)] public string VersionGuid { get; set; }
    }

    public sealed class CheckpointEntry
    {
        [JsonProperty("id", Required = Required.Always)] public string Id { get; set; }
        [JsonProperty("file", Required = Required.Always)] public string File { get; set; }
        [JsonProperty("label", Required = Required.Always)] public string Label { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("milestone", Required = Required.Always)] public bool Milestone { get; set; }
        [JsonProperty("created_utc", Required = Required.Always)] public string CreatedUtc { get; set; }
        [JsonProperty("bytes", Required = Required.Always)] public long Bytes { get; set; }
        [JsonProperty("source", Required = Required.Always)] public CheckpointSource Source { get; set; }
        [JsonProperty("revit_year", Required = Required.Always)] public int RevitYear { get; set; }
        [JsonProperty("user", Required = Required.Always)] public string User { get; set; }
    }

    public sealed class CheckpointIndexData
    {
        [JsonIgnore] public bool Exists { get; internal set; }
        [JsonProperty("schema", Required = Required.Always)] public int Schema { get; set; } = 1;
        [JsonProperty("model_file", Required = Required.Always)] public string ModelFile { get; set; }
        [JsonProperty("checkpoints", Required = Required.Always)] public List<CheckpointEntry> Checkpoints { get; set; } = new List<CheckpointEntry>();
    }

    public static class CheckpointIndex
    {
        public static CheckpointIndexData Load(string path, string modelFile)
        {
            var fullPath = Path.GetFullPath(path);
            return WithMutex(fullPath, () => LoadUnderMutex(fullPath, modelFile));
        }

        public static CheckpointIndexData Update(string path, string modelFile, Action<CheckpointIndexData> mutate)
        {
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));
            return Update(path, modelFile, data => { mutate(data); return true; });
        }

        public static CheckpointIndexData Update(string path, string modelFile, Func<CheckpointIndexData, bool> mutate)
        {
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));
            var fullPath = Path.GetFullPath(path);
            return WithMutex(fullPath, () =>
            {
                var data = LoadUnderMutex(fullPath, modelFile);
                if (!mutate(data)) return data;
                Validate(data);
                if (!string.Equals(data.ModelFile, modelFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Checkpoint index belongs to a different model.");
                WriteUnderMutex(fullPath, data);
                data.Exists = true;
                return data;
            });
        }

        private static T WithMutex<T>(string path, Func<T> action)
        {
            string name;
            using (var sha = SHA256.Create())
                name = "Local\\RvtMcp.CheckpointIndex." + BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, name))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new TimeoutException("CHECKPOINT_INDEX_BUSY");
                    return action();
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        private static CheckpointIndexData LoadUnderMutex(string path, string modelFile)
        {
            try
            {
                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) json = reader.ReadToEnd();
                var data = JsonConvert.DeserializeObject<CheckpointIndexData>(json);
                Validate(data);
                if (!string.Equals(data.ModelFile, modelFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Checkpoint index belongs to a different model.");
                data.Exists = true;
                return data;
            }
            catch (FileNotFoundException)
            {
                return new CheckpointIndexData { ModelFile = modelFile };
            }
            catch (DirectoryNotFoundException)
            {
                return new CheckpointIndexData { ModelFile = modelFile };
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Checkpoint index is unreadable.", ex);
            }
        }

        private static void WriteUnderMutex(string path, CheckpointIndexData data)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(data, Formatting.Indented));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void Validate(CheckpointIndexData data)
        {
            if (data == null || data.Schema != 1 || !IsFileName(data.ModelFile) || data.Checkpoints == null)
                throw new InvalidDataException("Checkpoint index is unreadable.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in data.Checkpoints)
            {
                if (entry == null || !IsFileName(entry.Id) || !IsFileName(entry.File)
                    || string.IsNullOrWhiteSpace(entry.Label) || entry.Label.Length > 80
                    || (entry.Reason != null && entry.Reason.Length > 500) || entry.Bytes < 0
                    || !DateTimeOffset.TryParse(entry.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || entry.Source == null || entry.Source.NumberOfSaves < 0 || !Guid.TryParse(entry.Source.VersionGuid, out _)
                    || entry.RevitYear <= 0 || entry.User == null || !ids.Add(entry.Id) || !files.Add(entry.File))
                    throw new InvalidDataException("Checkpoint index contains an invalid entry.");
            }
        }

        private static bool IsFileName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name != "." && name != ".."
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal);
        }
    }
}
