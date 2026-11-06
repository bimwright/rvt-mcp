using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace RvtMcp.Plugin.Documents
{
    public sealed class BackupLimitRecord
    {
        [JsonProperty("min_backups", Required = Required.Always)] public int MinBackups { get; set; }
        [JsonProperty("ensured_by", Required = Required.Always)] public string EnsuredBy { get; set; }
        [JsonProperty("recorded_utc", Required = Required.Always)] public string RecordedUtc { get; set; }
        [JsonProperty("revit_year", Required = Required.Always)] public int RevitYear { get; set; }
    }

    public sealed class BackupSaveDecision
    {
        public bool UseSaveAs { get; set; }
        public bool MustRecord { get; set; }
        public string EnsuredBy { get; set; }
    }

    public sealed class BackupLimitRegistry
    {
        public const int MinimumBackups = 20;
        private readonly string _path;
        private readonly string _mutexName;
        internal string MutexName => _mutexName;

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bimwright", "rvt-mcp", "documents", "backup-limits.json");

        public BackupLimitRegistry(string path = null)
        {
            _path = Path.GetFullPath(path ?? DefaultPath);
            using (var sha = SHA256.Create())
                _mutexName = "Local\\RvtMcp.BackupLimits." + BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(_path.ToUpperInvariant()))).Replace("-", "");
        }

        public BackupLimitRecord Get(string modelPath)
        {
            var records = Load();
            records.TryGetValue(Key(modelPath), out var record);
            return record;
        }

        // Record only after the selected native save has succeeded.
        public BackupSaveDecision Decide(string modelPath, int backupCount)
        {
            if (backupCount < 0) throw new ArgumentOutOfRangeException(nameof(backupCount));
            return Decide(modelPath, () => backupCount);
        }

        public BackupSaveDecision Decide(string modelPath, Func<int> countBackups)
        {
            if (countBackups == null) throw new ArgumentNullException(nameof(countBackups));
            var existing = Get(modelPath);
            if (existing != null) return new BackupSaveDecision { EnsuredBy = existing.EnsuredBy };
            var backupCount = countBackups();
            if (backupCount < 0) throw new ArgumentOutOfRangeException(nameof(countBackups));
            return new BackupSaveDecision
            {
                UseSaveAs = backupCount < MinimumBackups,
                MustRecord = true,
                EnsuredBy = backupCount < MinimumBackups ? "raised" : "observed"
            };
        }

        public bool Record(string modelPath, string ensuredBy, DateTimeOffset recordedUtc, int revitYear)
        {
            if (ensuredBy != "raised" && ensuredBy != "observed") throw new ArgumentException("Invalid backup evidence.", nameof(ensuredBy));
            if (revitYear <= 0) throw new ArgumentOutOfRangeException(nameof(revitYear));
            var key = Key(modelPath);
            using (var mutex = new Mutex(false, _mutexName))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return false;
                    var records = Load();
                    records[key] = new BackupLimitRecord
                    {
                        MinBackups = MinimumBackups,
                        EnsuredBy = ensuredBy,
                        RecordedUtc = recordedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        RevitYear = revitYear
                    };
                    Write(records);
                    return true;
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        private Dictionary<string, BackupLimitRecord> Load()
        {
            try
            {
                string json;
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) json = reader.ReadToEnd();
                var data = JsonConvert.DeserializeObject<Dictionary<string, BackupLimitRecord>>(json);
                if (data == null) throw new InvalidDataException("Registry is empty.");
                var result = new Dictionary<string, BackupLimitRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in data)
                {
                    var record = pair.Value;
                    if (string.IsNullOrWhiteSpace(pair.Key) || record == null || record.MinBackups != MinimumBackups
                        || (record.EnsuredBy != "raised" && record.EnsuredBy != "observed")
                        || !DateTimeOffset.TryParse(record.RecordedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                        || record.RevitYear <= 0 || result.ContainsKey(pair.Key))
                        throw new InvalidDataException("Registry contains an invalid record.");
                    result.Add(pair.Key, record);
                }
                return result;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                return new Dictionary<string, BackupLimitRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Write(Dictionary<string, BackupLimitRecord> records)
        {
            var directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(records, Formatting.Indented));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                try { File.Move(temporary, _path); }
                catch (IOException) { File.Replace(temporary, _path, null); }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static string Key(string modelPath) => Path.GetFullPath(modelPath).ToUpperInvariant();
    }
}
