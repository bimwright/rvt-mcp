using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Reports.History
{
    /// <summary>Exact, read-only joins of report capture references to gateway-owned SQLite History.</summary>
    public sealed class ReportHistoryLookup
    {
        private readonly string _root;
        public ReportHistoryLookup(string dataRoot = null) => _root = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");

        public JObject Read(string reportId, int callOffset = 0, int elementOffset = 0, int callLimit = 5, int elementLimit = 10)
        {
            if (reportId?.Length != 32 || !Guid.TryParseExact(reportId, "N", out _)) throw new ArgumentException("Invalid report_id.");
            if (callOffset < 0 || elementOffset < 0 || callLimit < 1 || callLimit > 10 || elementLimit < 1 || elementLimit > 25)
                throw new ArgumentException("Invalid History page; call_limit is 1-10 and element_limit is 1-25.");
            var directory = Path.Combine(_root, "reports");
            var paths = Directory.Exists(directory) ? Directory.GetFiles(directory, "*-" + reportId + ".json") : new string[0];
            if (paths.Length != 1) throw new IOException("Report is missing or ambiguous.");
            JObject report;
            using (var stream = new FileStream(paths[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 8 * 1024 * 1024) throw new IOException("Report is too large.");
                using (var reader = new StreamReader(stream)) report = JObject.Parse(reader.ReadToEnd());
            }
            if (report.Value<int?>("schema_version") != 1) throw new IOException("Unsupported report schema.");
            if (report.Value<string>("report_id") != reportId) throw new IOException("Report identity mismatch.");
            var refs = report["changes"]?["calls"] as JArray ?? new JArray();
            var calls = new JArray();
            foreach (var reference in refs.Skip(callOffset).Take(callLimit).OfType<JObject>())
                calls.Add(ReadCall(reference, elementOffset, elementLimit));
            return new JObject { ["report_id"] = reportId, ["scope"] = report["changes"]?["scope"] ?? report["source"]?["capture_scope"] ?? "process",
                ["status"] = refs.Count == 0 ? "no_provenance" : "references", ["total_calls"] = refs.Count,
                ["call_offset"] = callOffset, ["next_call_offset"] = callOffset + callLimit < refs.Count ? (JToken)(callOffset + callLimit) : JValue.CreateNull(),
                ["calls"] = calls };
        }

        private JObject ReadCall(JObject reference, int offset, int limit)
        {
            var call = reference.Value<string>("call_id");
            var session = reference.Value<string>("client_session");
            var model = reference.Value<string>("model_key");
            var result = new JObject { ["call_id"] = call, ["client_session"] = session, ["model_key"] = model,
                ["tool"] = reference["tool"]?.DeepClone(), ["status"] = "unavailable", ["elements"] = new JArray() };
            if (!IsId(call) || !IsId(session)) { result["status"] = "invalid_reference"; return result; }
            if (reference.Value<bool?>("history_requested") != true) { result["status"] = "disabled"; return result; }
            if (model?.Length != 64 || model.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
            { result["status"] = "model_unavailable"; return result; }
            var path = Path.Combine(_root, "projects", model + ".db");
            if (!File.Exists(path)) return result;
            try
            {
                using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 1, Pooling = false }.ToString()))
                {
                    db.Open();
                    using (var command = db.CreateCommand())
                    {
                        command.CommandText = @"SELECT c.at,c.success,c.complete,c.element_count,c.record_id,r.reason_known,substr(r.context_json,1,8193)
FROM change_calls c LEFT JOIN change_records r ON c.record_id=r.record_id WHERE c.call_id=$call AND c.session=$session";
                        command.Parameters.AddWithValue("$call", call); command.Parameters.AddWithValue("$session", session);
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read()) { result["status"] = "pending_or_missing"; return result; }
                            result["status"] = "available";
                            result["at"] = reader.GetString(0); result["success"] = reader.GetInt32(1) == 1;
                            result["complete"] = reader.GetInt32(2) == 1; result["total_elements"] = reader.GetInt32(3);
                            result["record_id"] = reader.IsDBNull(4) ? JValue.CreateNull() : (JToken)reader.GetString(4);
                            result["reason_status"] = reader.IsDBNull(4) ? "unassigned" : !reader.IsDBNull(5) && reader.GetInt32(5) == 1 ? "recorded" : "unknown";
                            result["reason"] = reader.IsDBNull(6) ? JValue.CreateNull() : Json(reader.GetString(6));
                        }
                    }
                    using (var command = db.CreateCommand())
                    {
                        command.CommandText = @"SELECT element_id,unique_id,kind,category,substr(before_json,1,8193),substr(after_json,1,8193)
FROM change_call_elements WHERE call_id=$call ORDER BY element_id LIMIT $limit OFFSET $offset";
                        command.Parameters.AddWithValue("$call", call); command.Parameters.AddWithValue("$limit", limit + 1); command.Parameters.AddWithValue("$offset", offset);
                        var elements = (JArray)result["elements"];
                        using (var reader = command.ExecuteReader())
                            while (reader.Read())
                            {
                                if (elements.Count == limit) { result["next_element_offset"] = offset + limit; break; }
                                elements.Add(new JObject { ["id"] = reader.GetInt64(0), ["unique_id"] = reader.IsDBNull(1) ? null : reader.GetString(1),
                                    ["kind"] = reader.GetString(2), ["category"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                                    ["before"] = reader.IsDBNull(4) ? JValue.CreateNull() : Json(reader.GetString(4)),
                                    ["after"] = reader.IsDBNull(5) ? JValue.CreateNull() : Json(reader.GetString(5)) });
                            }
                    }
                }
            }
            catch (Exception ex) when (ex is SqliteException || ex is IOException || ex is UnauthorizedAccessException)
            { result["status"] = "unavailable"; result["elements"] = new JArray(); }
            return result;
        }
        private static bool IsId(string id) => id?.Length == 32 && Guid.TryParseExact(id, "N", out _);
        private static JToken Json(string value)
        {
            if (value.Length <= 8192) { try { return JToken.Parse(value); } catch (JsonException) { } }
            return new JObject { ["truncated_or_invalid"] = true, ["preview"] = value.Substring(0, Math.Min(256, value.Length)) };
        }
    }
}
