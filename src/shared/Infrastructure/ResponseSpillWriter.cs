#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    public enum ResponseSpillFormat
    {
        Sqlite,
        Ndjson,
        Json,
        Text,
        Auto
    }

    public sealed class SpillCleanupResult
    {
        public int DeletedCount { get; set; }
        public int RemainingCount { get; set; }
        public int FailedCount { get; set; }
    }

    public sealed class ResponseSpillResult
    {
        public string Path { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public long ByteCount { get; set; }
        public long RecordCount { get; set; }
        public JObject Schema { get; set; } = new JObject();
        public string Preview { get; set; } = string.Empty;
        public bool PreviewTruncated { get; set; }
        public bool SchemaTruncated { get; set; }
        public JObject Envelope { get; set; } = new JObject();
    }

    /// <summary>
    /// Writes oversized command DTOs to a local, agent-readable spill artifact.
    /// The default directory is local to the machine running Revit.
    /// </summary>
    public sealed class ResponseSpillWriter
    {
        public const int PreviewMaxBytes = 48 * 1024;
        public const int SchemaMaxBytes = 64 * 1024;
        public const int MaxRetainedFiles = 50;
        public static readonly TimeSpan MaxFileAge = TimeSpan.FromHours(24);

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly string _directory;

        public ResponseSpillWriter()
            : this(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "rvt-mcp",
                "spill"))
        {
        }

        public ResponseSpillWriter(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Spill directory is required.", nameof(directory));
            _directory = System.IO.Path.GetFullPath(directory);
        }

        public ResponseSpillResult Write(string commandName, object? payload, ResponseSpillFormat format)
        {
            Directory.CreateDirectory(_directory);
            var token = payload as JToken ?? (payload == null ? JValue.CreateNull() : JToken.FromObject(payload));
            if (format == ResponseSpillFormat.Auto)
            {
                format = DetectFormat(token);
                if (token.Type == JTokenType.String && format != ResponseSpillFormat.Text)
                    token = JToken.Parse(token.Value<string>() ?? string.Empty);
            }

            switch (format)
            {
                case ResponseSpillFormat.Json:
                    return WriteTextArtifact(
                        commandName,
                        token.ToString(Formatting.None),
                        ".json",
                        "json",
                        token is JArray jsonArray ? jsonArray.Count : 1,
                        DescribeJsonSchema(token));
                case ResponseSpillFormat.Ndjson:
                    return WriteNdjson(commandName, token);
                case ResponseSpillFormat.Sqlite:
                    return WriteSqlite(commandName, token);
                case ResponseSpillFormat.Text:
                    return WriteTextArtifact(
                        commandName,
                        token.Type == JTokenType.String ? token.Value<string>() ?? string.Empty : token.ToString(Formatting.None),
                        ".txt",
                        "text",
                        1,
                        new JObject { ["root"] = new JArray("text") });
                default:
                    throw new NotSupportedException("Spill format is not implemented: " + format);
            }
        }

        public static ResponseSpillFormat DetectFormat(object? payload)
        {
            var token = payload as JToken ?? (payload == null ? JValue.CreateNull() : JToken.FromObject(payload));
            if (token.Type == JTokenType.String)
            {
                var text = token.Value<string>() ?? string.Empty;
                try { return DetectFormat(JToken.Parse(text)); }
                catch (JsonException) { return ResponseSpillFormat.Text; }
            }

            if (token is JObject)
                return ResponseSpillFormat.Json;
            if (!(token is JArray array))
                return ResponseSpillFormat.Text;
            if (array.Count <= 1)
                return ResponseSpillFormat.Ndjson;

            var firstSignature = RecordSignature(array[0]);
            return array.Skip(1).All(item => string.Equals(RecordSignature(item), firstSignature, StringComparison.Ordinal))
                ? ResponseSpillFormat.Ndjson
                : ResponseSpillFormat.Json;
        }

        private static string RecordSignature(JToken token)
        {
            if (token is JObject obj)
                return "object:" + string.Join("|", obj.Properties().Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal));
            return token.Type.ToString();
        }

        private ResponseSpillResult WriteNdjson(string commandName, JToken token)
        {
            var lines = new System.Collections.Generic.List<string>();
            var schema = new JObject();

            if (token is JArray rootArray)
            {
                foreach (var item in rootArray)
                    lines.Add(item.ToString(Formatting.None));
                schema["records"] = ColumnNames(rootArray);
            }
            else if (token is JObject rootObject)
            {
                var rootRecord = new JObject { ["_record_type"] = "root" };
                foreach (var property in rootObject.Properties())
                {
                    if (!(property.Value is JArray))
                        rootRecord[property.Name] = property.Value.DeepClone();
                }
                if (rootRecord.Count > 1)
                {
                    lines.Add(rootRecord.ToString(Formatting.None));
                    schema["_root"] = new JArray(rootRecord.Properties().Select(property => property.Name));
                }

                foreach (var property in rootObject.Properties())
                {
                    if (!(property.Value is JArray collection))
                        continue;
                    schema[property.Name] = ColumnNames(collection);
                    for (var i = 0; i < collection.Count; i++)
                    {
                        lines.Add(new JObject
                        {
                            ["_record_type"] = "collection_item",
                            ["_collection"] = property.Name,
                            ["_index"] = i,
                            ["data"] = collection[i].DeepClone()
                        }.ToString(Formatting.None));
                    }
                }
            }
            else
            {
                lines.Add(new JObject
                {
                    ["_record_type"] = "value",
                    ["data"] = token.DeepClone()
                }.ToString(Formatting.None));
                schema["records"] = new JArray("data");
            }

            var content = lines.Count == 0 ? string.Empty : string.Join("\n", lines) + "\n";
            return WriteTextArtifact(commandName, content, ".ndjson", "ndjson", lines.Count, schema);
        }

        private ResponseSpillResult WriteTextArtifact(
            string commandName,
            string content,
            string extension,
            string format,
            long recordCount,
            JObject schema)
        {
            var path = BuildPath(commandName, extension);
            File.WriteAllText(path, content, Utf8NoBom);
            Cleanup(DateTime.UtcNow, path);
            var preview = TakeUtf8Prefix(content, PreviewMaxBytes, out var previewTruncated);
            var result = new ResponseSpillResult
            {
                Path = path,
                Format = format,
                ByteCount = new FileInfo(path).Length,
                RecordCount = recordCount,
                Schema = schema,
                Preview = preview,
                PreviewTruncated = previewTruncated
            };
            result.Envelope = BuildEnvelope(result);
            return result;
        }

        private static JObject DescribeJsonSchema(JToken token)
        {
            var schema = new JObject();
            DescribeJsonToken(token, "root", schema);
            return schema;
        }

        private static void DescribeJsonToken(JToken token, string path, JObject schema)
        {
            if (token is JObject obj)
            {
                MergeSchemaColumns(schema, path, obj.Properties().Select(property => property.Name));
                foreach (var property in obj.Properties())
                {
                    if (property.Value is JObject || property.Value is JArray)
                        DescribeJsonToken(property.Value, path + "_" + SanitizeSqlName(property.Name, "value"), schema);
                }
                return;
            }

            if (token is JArray array)
            {
                var objects = array.OfType<JObject>().ToArray();
                if (objects.Length > 0)
                {
                    MergeSchemaColumns(schema, path, objects.SelectMany(item => item.Properties().Select(property => property.Name)));
                    foreach (var item in objects)
                    {
                        foreach (var property in item.Properties())
                        {
                            if (property.Value is JObject || property.Value is JArray)
                                DescribeJsonToken(property.Value, path + "_" + SanitizeSqlName(property.Name, "value"), schema);
                        }
                    }
                }
                else
                {
                    MergeSchemaColumns(schema, path, new[] { "value" });
                }
                return;
            }

            MergeSchemaColumns(schema, path, new[] { "value" });
        }

        private static void MergeSchemaColumns(JObject schema, string path, IEnumerable<string> names)
        {
            var columns = schema[path] as JArray ?? new JArray();
            var known = new HashSet<string>(columns.Select(column => column.Value<string>() ?? string.Empty), StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (known.Add(name))
                    columns.Add(name);
            }
            schema[path] = columns;
        }

        private static JArray ColumnNames(JArray collection)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var obj in collection.OfType<JObject>())
            {
                foreach (var property in obj.Properties())
                    names.Add(property.Name);
            }
            return new JArray(names);
        }

        private ResponseSpillResult WriteSqlite(string commandName, JToken token)
        {
            var tables = Relationalize(token);
            var path = BuildPath(commandName, ".sqlite");

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString()))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var table in tables)
                        WriteSqliteTable(connection, transaction, table);
                    transaction.Commit();
                }
            }

            Cleanup(DateTime.UtcNow, path);

            var schema = new JObject();
            var previewObject = new JObject();
            long recordCount = 0;
            foreach (var table in tables)
            {
                schema[table.Name] = table.Columns.Count == 0
                    ? new JArray("_row")
                    : new JArray(table.Columns);
                if (!string.Equals(table.Name, "_root", StringComparison.Ordinal))
                    recordCount += table.Rows.Count;

                var previewRows = new JArray();
                foreach (var row in table.Rows.Take(20))
                {
                    var previewRow = new JObject();
                    foreach (var column in table.Columns)
                        previewRow[column] = row.TryGetValue(column, out var value) ? value.DeepClone() : JValue.CreateNull();
                    previewRows.Add(previewRow);
                }
                previewObject[table.Name] = previewRows;
            }

            var preview = TakeUtf8Prefix(previewObject.ToString(Formatting.None), PreviewMaxBytes, out var previewTruncated);
            var result = new ResponseSpillResult
            {
                Path = path,
                Format = "sqlite",
                ByteCount = new FileInfo(path).Length,
                RecordCount = recordCount,
                Schema = schema,
                Preview = preview,
                PreviewTruncated = previewTruncated
            };
            result.Envelope = BuildEnvelope(result);
            return result;
        }

        private static List<RelationalTable> Relationalize(JToken token)
        {
            var tables = new List<RelationalTable>();
            if (token is JObject root)
            {
                var rootRow = new Dictionary<string, JToken>(StringComparer.Ordinal);
                foreach (var property in root.Properties())
                {
                    if (property.Value is JArray collection)
                        CollectArray(tables, SanitizeSqlName(property.Name, "records"), collection, null, null);
                    else if (property.Value is JObject nested)
                        FlattenObject(rootRow, property.Name, nested);
                    else
                        AddColumn(rootRow, property.Name, property.Value);
                }
                if (rootRow.Count > 0)
                {
                    var rootTable = new RelationalTable("_root");
                    rootTable.AddRow(rootRow);
                    tables.Insert(0, rootTable);
                }
            }
            else if (token is JArray array)
            {
                CollectArray(tables, "records", array, null, null);
            }
            else
            {
                var rootTable = new RelationalTable("_root");
                rootTable.AddRow(new Dictionary<string, JToken>(StringComparer.Ordinal) { ["value"] = token });
                tables.Add(rootTable);
            }

            if (tables.Count == 0)
                tables.Add(new RelationalTable("_root"));
            return tables;
        }

        private static void CollectArray(
            List<RelationalTable> tables,
            string tableName,
            JArray collection,
            string? parentTable,
            int? parentRow)
        {
            var table = tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));
            if (table == null)
            {
                table = new RelationalTable(tableName);
                tables.Add(table);
            }

            foreach (var item in collection)
            {
                var row = new Dictionary<string, JToken>(StringComparer.Ordinal);
                if (parentTable != null)
                {
                    row["_parent_table"] = parentTable;
                    row["_parent_row"] = parentRow.HasValue ? new JValue(parentRow.Value) : JValue.CreateNull();
                }

                var nestedCollections = new List<Tuple<string, JArray>>();
                if (item is JObject obj)
                {
                    foreach (var property in obj.Properties())
                    {
                        if (property.Value is JArray nestedArray)
                            nestedCollections.Add(Tuple.Create(property.Name, nestedArray));
                        else if (property.Value is JObject nestedObject)
                            FlattenObject(row, property.Name, nestedObject);
                        else
                            AddColumn(row, property.Name, property.Value);
                    }
                }
                else
                {
                    row["value"] = item;
                }

                var rowIndex = table.Rows.Count;
                table.AddRow(row);
                foreach (var nested in nestedCollections)
                {
                    CollectArray(
                        tables,
                        SanitizeSqlName(tableName + "_" + nested.Item1, "records"),
                        nested.Item2,
                        table.Name,
                        rowIndex);
                }
            }
        }

        private static void FlattenObject(Dictionary<string, JToken> row, string prefix, JObject value)
        {
            foreach (var property in value.Properties())
            {
                var name = prefix + "_" + property.Name;
                if (property.Value is JObject nested)
                    FlattenObject(row, name, nested);
                else if (property.Value is JArray array)
                    AddColumn(row, name, new JValue(array.ToString(Formatting.None)));
                else
                    AddColumn(row, name, property.Value);
            }
        }

        private static void AddColumn(Dictionary<string, JToken> row, string rawName, JToken value)
        {
            var baseName = SanitizeSqlName(rawName, "value");
            var name = baseName;
            var suffix = 2;
            while (row.ContainsKey(name))
                name = baseName + "_" + (suffix++).ToString(CultureInfo.InvariantCulture);
            row[name] = value;
        }

        private static void WriteSqliteTable(SqliteConnection connection, SqliteTransaction transaction, RelationalTable table)
        {
            var columns = table.Columns.Count == 0 ? new[] { "_row" } : table.Columns.ToArray();
            var definitions = columns.Select(column => QuoteSqlIdentifier(column) + " " + InferSqliteType(table, column));
            using (var create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = "CREATE TABLE " + QuoteSqlIdentifier(table.Name) + " (" + string.Join(", ", definitions) + ")";
                create.ExecuteNonQuery();
            }

            if (table.Rows.Count == 0)
                return;

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO " + QuoteSqlIdentifier(table.Name)
                    + " (" + string.Join(", ", columns.Select(QuoteSqlIdentifier)) + ") VALUES ("
                    + string.Join(", ", columns.Select((_, index) => "$p" + index.ToString(CultureInfo.InvariantCulture))) + ")";
                for (var i = 0; i < columns.Length; i++)
                    insert.Parameters.Add(new SqliteParameter("$p" + i.ToString(CultureInfo.InvariantCulture), null));

                foreach (var row in table.Rows)
                {
                    for (var i = 0; i < columns.Length; i++)
                    {
                        insert.Parameters[i].Value = row.TryGetValue(columns[i], out var value)
                            ? ToSqliteValue(value)
                            : DBNull.Value;
                    }
                    insert.ExecuteNonQuery();
                }
            }
        }

        private static string InferSqliteType(RelationalTable table, string column)
        {
            var sawReal = false;
            foreach (var row in table.Rows)
            {
                if (!row.TryGetValue(column, out var value) || value.Type == JTokenType.Null)
                    continue;
                if (value.Type == JTokenType.Integer || value.Type == JTokenType.Boolean)
                    continue;
                if (value.Type == JTokenType.Float)
                {
                    sawReal = true;
                    continue;
                }
                return "TEXT";
            }
            return sawReal ? "REAL" : "INTEGER";
        }

        private static object ToSqliteValue(JToken value)
        {
            switch (value.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return DBNull.Value;
                case JTokenType.Boolean:
                    return value.Value<bool>() ? 1L : 0L;
                case JTokenType.Integer:
                    return value.Value<long>();
                case JTokenType.Float:
                    return value.Value<double>();
                case JTokenType.String:
                    return value.Value<string>() ?? string.Empty;
                default:
                    return value.ToString(Formatting.None);
            }
        }

        private static string QuoteSqlIdentifier(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string SanitizeSqlName(string? value, string fallback)
        {
            var builder = new StringBuilder();
            foreach (var c in value ?? string.Empty)
                builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return builder.Length == 0 ? fallback : builder.ToString();
        }

        private sealed class RelationalTable
        {
            private readonly HashSet<string> _columnSet = new HashSet<string>(StringComparer.Ordinal);

            public RelationalTable(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public List<string> Columns { get; } = new List<string>();
            public List<Dictionary<string, JToken>> Rows { get; } = new List<Dictionary<string, JToken>>();

            public void AddRow(Dictionary<string, JToken> row)
            {
                foreach (var column in row.Keys)
                {
                    if (_columnSet.Add(column))
                        Columns.Add(column);
                }
                Rows.Add(row);
            }
        }

        public SpillCleanupResult Cleanup(DateTime utcNow)
        {
            return Cleanup(utcNow, null);
        }

        private SpillCleanupResult Cleanup(DateTime utcNow, string? protectedPath)
        {
            var result = new SpillCleanupResult();
            if (!Directory.Exists(_directory))
                return result;

            var cutoff = utcNow.ToUniversalTime().Subtract(MaxFileAge);
            var retained = new List<FileInfo>();
            foreach (var path in Directory.GetFiles(_directory))
            {
                var file = new FileInfo(path);
                var isProtected = protectedPath != null
                    && string.Equals(file.FullName, protectedPath, StringComparison.OrdinalIgnoreCase);
                if (!isProtected && file.LastWriteTimeUtc < cutoff)
                {
                    if (TryDelete(file.FullName)) result.DeletedCount++;
                    else result.FailedCount++;
                }
                else
                {
                    retained.Add(file);
                }
            }

            var ordered = retained
                .Where(file => file.Exists)
                .OrderByDescending(file => protectedPath != null
                    && string.Equals(file.FullName, protectedPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .ToArray();
            for (var i = MaxRetainedFiles; i < ordered.Length; i++)
            {
                if (TryDelete(ordered[i].FullName)) result.DeletedCount++;
                else result.FailedCount++;
            }

            try { result.RemainingCount = Directory.GetFiles(_directory).Length; }
            catch { result.RemainingCount = Math.Min(ordered.Length, MaxRetainedFiles) + result.FailedCount; }
            return result;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string BuildPath(string commandName, string extension)
        {
            var safeCommand = SanitizeFilePart(commandName);
            var utc = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            var entropy = Guid.NewGuid().ToString("N");
            string shortHash;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Utf8NoBom.GetBytes(safeCommand + "|" + utc + "|" + entropy));
                shortHash = BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
            var fileName = safeCommand + "_" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                + "_" + utc + "_" + shortHash + extension;
            return System.IO.Path.Combine(_directory, fileName);
        }

        private static string SanitizeFilePart(string? value)
        {
            var input = string.IsNullOrWhiteSpace(value) ? "response" : value!;
            var builder = new StringBuilder(Math.Min(input.Length, 48));
            foreach (var c in input)
            {
                if (builder.Length >= 48)
                    break;
                builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return builder.Length == 0 ? "response" : builder.ToString();
        }

        private static JObject BuildEnvelope(ResponseSpillResult result)
        {
            result.Schema = BoundSchema(result.Schema, out var schemaTruncated);
            result.SchemaTruncated = schemaTruncated;
            return new JObject
            {
                ["success"] = true,
                ["output_mode"] = "file",
                ["path"] = result.Path,
                ["format"] = result.Format,
                ["byte_count"] = result.ByteCount,
                ["schema"] = result.Schema,
                ["schema_truncated"] = result.SchemaTruncated,
                ["record_count"] = result.RecordCount,
                ["preview"] = result.Preview,
                ["preview_truncated"] = result.PreviewTruncated,
                ["note"] = "Local same-machine file. Query with SQL/jq or another local tool; do not re-call this command for the full dataset."
            };
        }

        private static JObject BoundSchema(JObject source, out bool truncated)
        {
            if (Utf8NoBom.GetByteCount(source.ToString(Formatting.None)) <= SchemaMaxBytes)
            {
                truncated = false;
                return source;
            }

            truncated = true;
            var bounded = new JObject();
            var usedBytes = 2;
            var omitted = 0;
            foreach (var property in source.Properties())
            {
                if (!(property.Value is JArray columns))
                {
                    omitted++;
                    continue;
                }

                var kept = new JArray();
                foreach (var column in columns)
                {
                    var columnText = column.ToString(Formatting.None);
                    var nextBytes = Utf8NoBom.GetByteCount(columnText) + 2;
                    if (usedBytes + nextBytes > SchemaMaxBytes - 256)
                    {
                        omitted += columns.Count - kept.Count;
                        break;
                    }
                    kept.Add(column.DeepClone());
                    usedBytes += nextBytes;
                }

                if (kept.Count > 0)
                {
                    bounded[property.Name] = kept;
                    usedBytes += Utf8NoBom.GetByteCount(property.Name) + 6;
                }

                if (usedBytes >= SchemaMaxBytes - 256)
                    break;
            }
            bounded["_truncated"] = "Schema column list was bounded; inspect SQLite PRAGMA table_info(...) for the complete schema. Omitted entries: "
                + omitted.ToString(CultureInfo.InvariantCulture) + ".";
            return bounded;
        }

        private static string TakeUtf8Prefix(string value, int maxBytes, out bool truncated)
        {
            var bytes = Utf8NoBom.GetBytes(value);
            if (bytes.Length <= maxBytes)
            {
                truncated = false;
                return value;
            }

            var length = maxBytes;
            while (length > 0 && (bytes[length] & 0xC0) == 0x80)
                length--;
            truncated = true;
            return Utf8NoBom.GetString(bytes, 0, length);
        }
    }
}
