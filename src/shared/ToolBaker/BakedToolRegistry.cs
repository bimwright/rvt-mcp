using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.ToolBaker
{
    public class BakedToolMeta
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string ParametersSchema { get; set; }
        public string DisplayName { get; set; }
        public string Source { get; set; }
        public string OutputChoice { get; set; }
        public string CreatedUtc { get; set; }
        public int CallCount { get; set; }
        public int UsageCount { get; set; }
        public int UsageScore30d { get; set; }
        public string LastUsedAt { get; set; }
        public double FailureRate { get; set; }
        public string CompatMap { get; set; }
        public string LifecycleState { get; set; }
    }

    public class BakedToolRegistry
    {
        private readonly string _dir;
        private readonly string _dbPath;
        private readonly string _legacyRegistryPath;
        private readonly Dictionary<string, BakedToolMeta> _tools = new Dictionary<string, BakedToolMeta>();
        private readonly Dictionary<string, string> _sources = new Dictionary<string, string>();
        private readonly object _lock = new object();

        public BakedToolRegistry()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "rvt-mcp", "baked"))
        {
        }

        internal BakedToolRegistry(string bakedDir)
        {
            if (string.IsNullOrWhiteSpace(bakedDir))
                throw new ArgumentException("Baked tool directory is required.", nameof(bakedDir));

            _dir = bakedDir;
            _legacyRegistryPath = Path.Combine(_dir, "registry.json");
            var root = Directory.GetParent(_dir)?.FullName ?? _dir;
            _dbPath = Path.Combine(root, "bake.db");
            Load();
        }

        public string BakedDir => _dir;

        public void Save(BakedToolMeta meta, string sourceCode)
        {
            if (meta == null || string.IsNullOrWhiteSpace(meta.Name))
                return;

            lock (_lock)
            {
                _tools[meta.Name] = meta;
                _sources[meta.Name] = sourceCode;
            }
        }

        public string GetSource(string name)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(name, out var source))
                    return source;
            }

            var path = Path.Combine(_dir, name + ".cs");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public BakedToolMeta GetMeta(string name)
        {
            lock (_lock)
            {
                LoadBakeDb();
                _tools.TryGetValue(name, out var meta);
                return meta;
            }
        }

        public IEnumerable<BakedToolMeta> GetAll()
        {
            lock (_lock)
            {
                LoadBakeDb();
                return new List<BakedToolMeta>(_tools.Values);
            }
        }

        public IEnumerable<BakedToolMeta> GetAllSortedForList()
        {
            lock (_lock)
            {
                LoadBakeDb();
                return _tools.Values
                    .OrderByDescending(t => t.UsageScore30d)
                    .ThenByDescending(t => ParseTimestamp(t.LastUsedAt))
                    .ThenBy(t => t.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        public void IncrementCallCount(string name)
        {
            lock (_lock)
            {
                if (_tools.TryGetValue(name, out var meta))
                {
                    meta.CallCount++;
                    meta.UsageCount++;
                }
            }
        }

        public void RecordRun(string name, string revitVersion, bool success, string error)
        {
            lock (_lock)
            {
                if (!_tools.TryGetValue(name, out var meta))
                    return;

                var now = DateTimeOffset.UtcNow.ToString("o");
                meta.LastUsedAt = now;
                if (success)
                {
                    meta.CallCount++;
                    meta.UsageCount++;
                    meta.UsageScore30d++;
                }

                meta.CompatMap = UpdateCompatMap(meta.CompatMap, revitVersion, success, error, now);
            }
        }

        public bool Remove(string name)
        {
            lock (_lock)
            {
                if (!_tools.Remove(name)) return false;
                _sources.Remove(name);
                return true;
            }
        }

        private void Load()
        {
            LoadBakeDb();
            LoadLegacyJson();
        }

        private void LoadBakeDb()
        {
            if (!File.Exists(_dbPath))
                return;

            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = _dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared,
                    Pooling = false,
                }.ToString());
                connection.Open();
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }

                var columns = RegistryColumns(connection);
                var hasUsageCount = columns.Contains("usage_count");
                var hasLifecycleState = columns.Contains("lifecycle_state");
                using var command = connection.CreateCommand();
                command.CommandText = hasUsageCount && hasLifecycleState
                    ? @"
SELECT name, description, source, params_schema, compat_map, created_at, source_code,
       last_used_at, usage_count, failure_rate, lifecycle_state, version_history_blob
FROM registry
ORDER BY name"
                    : @"
SELECT name, description, source, params_schema, compat_map, created_at, source_code,
       last_used_at, 0 AS usage_count, failure_rate, 'accepted' AS lifecycle_state, version_history_blob
FROM registry
ORDER BY name";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    var compat = ParseCompatMap(reader.GetString(4));
                    _tools[name] = new BakedToolMeta
                    {
                        Name = name,
                        Description = reader.GetString(1),
                        Source = reader.GetString(2),
                        ParametersSchema = reader.GetString(3),
                        DisplayName = (string)compat["display_name"] ?? name,
                        OutputChoice = (string)compat["output_choice"] ?? "mcp_only",
                        CreatedUtc = reader.GetString(5),
                        LastUsedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
                        UsageCount = Convert.ToInt32(reader.GetValue(8)),
                        FailureRate = Convert.ToDouble(reader.GetValue(9)),
                        LifecycleState = reader.GetString(10),
                        CompatMap = reader.GetString(4),
                        UsageScore30d = UsageScore30d(reader.GetString(11), Convert.ToInt32(reader.GetValue(8))),
                    };

                    if (!reader.IsDBNull(6))
                        _sources[name] = reader.GetString(6);
                }
            }
            catch
            {
                // Runtime facade must not mutate durable storage or block plugin startup.
            }
        }

        private static JObject ParseCompatMap(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new JObject();
            try
            {
                return JObject.Parse(json);
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }

        private static HashSet<string> RegistryColumns(SqliteConnection connection)
        {
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('registry')";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(0));
            return columns;
        }

        private static int UsageScore30d(string versionHistoryBlob, int usageCount)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            var sawRunEvent = false;
            var count = 0;
            try
            {
                foreach (var entry in JArray.Parse(string.IsNullOrWhiteSpace(versionHistoryBlob) ? "[]" : versionHistoryBlob).OfType<JObject>())
                {
                    var evt = entry.Value<string>("event");
                    if (evt != "run_succeeded" && evt != "run_failed")
                        continue;

                    sawRunEvent = true;
                    if (evt == "run_succeeded" &&
                        DateTimeOffset.TryParse(entry.Value<string>("at"), out var at) &&
                        at >= cutoff)
                    {
                        count++;
                    }
                }
            }
            catch (JsonException)
            {
                return usageCount;
            }

            return sawRunEvent ? count : usageCount;
        }

        private static DateTimeOffset ParseTimestamp(string value)
        {
            return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : DateTimeOffset.MinValue;
        }

        private static string UpdateCompatMap(string compatMap, string revitVersion, bool success, string error, string now)
        {
            var compat = ParseCompatMap(compatMap);
            if (!(compat["tested"] is JObject tested))
            {
                tested = new JObject();
                compat["tested"] = tested;
            }

            var version = string.IsNullOrWhiteSpace(revitVersion) ? "unknown" : revitVersion;
            var entry = new JObject
            {
                ["ok"] = success,
                ["last_run"] = now
            };
            if (!success && !string.IsNullOrWhiteSpace(error))
                entry["last_error"] = error;
            tested[version] = entry;
            return compat.ToString(Formatting.None);
        }

        private void LoadLegacyJson()
        {
            if (!File.Exists(_legacyRegistryPath))
                return;

            try
            {
                var json = File.ReadAllText(_legacyRegistryPath);
                var list = JsonConvert.DeserializeObject<List<BakedToolMeta>>(json);
                if (list == null) return;
                foreach (var meta in list)
                {
                    if (meta == null || string.IsNullOrWhiteSpace(meta.Name))
                        continue;

                    if (!_tools.ContainsKey(meta.Name))
                        _tools[meta.Name] = meta;
                }
            }
            catch
            {
                // Runtime facade must not quarantine or write sidecar diagnostics.
            }
        }
    }
}
