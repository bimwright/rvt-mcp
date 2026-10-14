using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ResponseSpillWriterTests : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "rvtmcp-spill-tests-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Json_spill_writes_exact_file_and_bounded_local_envelope()
        {
            var writer = new ResponseSpillWriter(_directory);
            var payload = new JObject
            {
                ["groups"] = new JArray(
                    new JObject { ["name"] = "Identity", ["definitions"] = new JArray("Mark", "Comments") })
            };

            var spill = writer.Write("export_shared_parameter_file", payload, ResponseSpillFormat.Json);

            Assert.True(File.Exists(spill.Path));
            Assert.Equal("json", spill.Format);
            Assert.Equal(new FileInfo(spill.Path).Length, spill.ByteCount);
            Assert.Equal(payload.ToString(Newtonsoft.Json.Formatting.None), File.ReadAllText(spill.Path, Encoding.UTF8));
            Assert.Equal("file", spill.Envelope.Value<string>("output_mode"));
            Assert.Equal(spill.Path, spill.Envelope.Value<string>("path"));
            Assert.True(spill.Envelope.Value<bool>("success"));
            Assert.Contains("groups", spill.Schema["root"]!.Values<string>());
            Assert.Contains("name", spill.Schema["root_groups"]!.Values<string>());
            Assert.Contains("definitions", spill.Schema["root_groups"]!.Values<string>());
            Assert.True(Encoding.UTF8.GetByteCount(spill.Envelope.ToString(Newtonsoft.Json.Formatting.None)) < ResponseSizeGuard.EnforcementBudgetBytes);
        }

        [Fact]
        public void Ndjson_spill_preserves_root_metadata_and_writes_one_collection_item_per_line()
        {
            var writer = new ResponseSpillWriter(_directory);
            var payload = new JObject
            {
                ["rolledBack"] = false,
                ["results"] = new JArray(
                    new JObject { ["index"] = 0, ["ok"] = true },
                    new JObject { ["index"] = 1, ["ok"] = false, ["error"] = "failed" })
            };

            var spill = writer.Write("batch_execute", payload, ResponseSpillFormat.Ndjson);

            var lines = File.ReadAllLines(spill.Path, Encoding.UTF8);
            Assert.Equal(3, lines.Length);
            Assert.Equal("root", JObject.Parse(lines[0]).Value<string>("_record_type"));
            Assert.False(JObject.Parse(lines[0]).Value<bool>("rolledBack"));
            Assert.Equal("results", JObject.Parse(lines[1]).Value<string>("_collection"));
            Assert.Equal(1, JObject.Parse(lines[2])["data"]!.Value<int>("index"));
            Assert.Equal(3, spill.RecordCount);
            Assert.Equal("ndjson", spill.Format);
            Assert.Contains("rolledBack", spill.Schema["_root"]!.Values<string>());
        }

        [Fact]
        public void Sqlite_spill_relationalizes_collections_with_typed_columns_and_json_preview()
        {
            var writer = new ResponseSpillWriter(_directory);
            var payload = new JObject
            {
                ["projectName"] = "Tower A",
                ["totalRooms"] = 2,
                ["rooms"] = new JArray(
                    new JObject
                    {
                        ["elementId"] = 101L,
                        ["name"] = "Lobby",
                        ["areaMsq"] = 12.5,
                        ["boundary_materials"] = new JArray(new JObject
                        {
                            ["element_id"] = 501L,
                            ["material_names"] = new JArray("Concrete", "Paint")
                        })
                    },
                    new JObject { ["elementId"] = 102L, ["name"] = "Office", ["areaMsq"] = 20.0 })
            };

            var spill = writer.Write("export_room_data", payload, ResponseSpillFormat.Sqlite);

            using var connection = new SqliteConnection("Data Source=" + spill.Path + ";Mode=ReadOnly");
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM \"rooms\"";
            Assert.Equal(2L, (long)count.ExecuteScalar()!);

            using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT name, type FROM pragma_table_info('rooms') ORDER BY cid";
            using var reader = columns.ExecuteReader();
            var typedColumns = new System.Collections.Generic.Dictionary<string, string>();
            while (reader.Read()) typedColumns[reader.GetString(0)] = reader.GetString(1);
            Assert.Equal("INTEGER", typedColumns["elementId"]);
            Assert.Equal("TEXT", typedColumns["name"]);
            Assert.Equal("REAL", typedColumns["areaMsq"]);
            Assert.Contains("rooms", spill.Schema.Properties().Select(p => p.Name));
            Assert.Contains("rooms_boundary_materials", spill.Schema.Properties().Select(p => p.Name));
            Assert.Contains("rooms_boundary_materials_material_names", spill.Schema.Properties().Select(p => p.Name));
            using var nestedCount = connection.CreateCommand();
            nestedCount.CommandText = "SELECT COUNT(*) FROM \"rooms_boundary_materials_material_names\" WHERE \"_parent_row\" = 0";
            Assert.Equal(2L, (long)nestedCount.ExecuteScalar()!);
            Assert.StartsWith("{", spill.Preview);
            Assert.Equal(5, spill.RecordCount);
            Assert.Equal("sqlite", spill.Format);
        }

        [Fact]
        public void Auto_spill_sniffs_homogeneous_arrays_json_objects_and_plain_text()
        {
            var homogeneous = new JArray(
                new JObject { ["id"] = 1, ["name"] = "A" },
                new JObject { ["id"] = 2, ["name"] = "B" });
            var heterogeneous = new JArray(1, new JObject { ["id"] = 2 });

            Assert.Equal(ResponseSpillFormat.Ndjson, ResponseSpillWriter.DetectFormat(homogeneous));
            Assert.Equal(ResponseSpillFormat.Json, ResponseSpillWriter.DetectFormat(heterogeneous));
            Assert.Equal(ResponseSpillFormat.Json, ResponseSpillWriter.DetectFormat(new JObject { ["id"] = 1 }));
            Assert.Equal(ResponseSpillFormat.Text, ResponseSpillWriter.DetectFormat("plain output"));

            var spill = new ResponseSpillWriter(_directory).Write("send_code_to_revit", "plain output", ResponseSpillFormat.Auto);
            Assert.Equal("text", spill.Format);
            Assert.Equal("plain output", File.ReadAllText(spill.Path, Encoding.UTF8));
        }

        [Fact]
        public void Preview_is_utf8_safe_and_keeps_the_envelope_below_enforcement_budget()
        {
            var payload = string.Concat(Enumerable.Repeat("🏗️", 30000));

            var spill = new ResponseSpillWriter(_directory).Write("send_code_to_revit", payload, ResponseSpillFormat.Text);

            Assert.True(spill.PreviewTruncated);
            Assert.True(Encoding.UTF8.GetByteCount(spill.Preview) <= ResponseSpillWriter.PreviewMaxBytes);
            Assert.DoesNotContain("�", spill.Preview);
            Assert.True(Encoding.UTF8.GetByteCount(spill.Envelope.ToString(Newtonsoft.Json.Formatting.None)) < ResponseSizeGuard.EnforcementBudgetBytes);
        }

        private void WriteAged(string name, DateTime now, double ageHours)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, now.AddHours(-ageHours));
        }

        [Fact]
        public void Default_retention_is_36_hours()
        {
            Assert.Equal(TimeSpan.FromHours(36), ResponseSpillWriter.DefaultRetention);
            Assert.Equal(TimeSpan.FromHours(36), new ResponseSpillWriter(_directory).Retention);
        }

        [Fact]
        public void Cleanup_deletes_only_files_older_than_36_hours()
        {
            var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            WriteAged("age-35h.json", now, 35);
            WriteAged("age-37h.json", now, 37);

            var cleanup = new ResponseSpillWriter(_directory).Cleanup(now);

            Assert.Equal(1, cleanup.DeletedCount);
            Assert.Equal(1, cleanup.RemainingCount);
            Assert.True(File.Exists(Path.Combine(_directory, "age-35h.json")));
            Assert.False(File.Exists(Path.Combine(_directory, "age-37h.json")));
        }

        [Fact]
        public void Cleanup_never_deletes_a_file_inside_retention_however_many_files_exist()
        {
            var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 120; i++)
                WriteAged("recent-" + i + ".json", now, i % 35 + 0.5);

            var cleanup = new ResponseSpillWriter(_directory).Cleanup(now);

            Assert.Equal(0, cleanup.DeletedCount);
            Assert.Equal(120, cleanup.RemainingCount);
            Assert.Equal(120, Directory.GetFiles(_directory).Length);
        }

        [Fact]
        public void Cleanup_follows_a_custom_retention()
        {
            var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            WriteAged("age-35h.json", now, 35);
            WriteAged("age-47h.json", now, 47);
            WriteAged("age-49h.json", now, 49);

            var cleanup = new ResponseSpillWriter(_directory, TimeSpan.FromHours(48)).Cleanup(now);

            Assert.Equal(1, cleanup.DeletedCount);
            Assert.True(File.Exists(Path.Combine(_directory, "age-47h.json")));
            Assert.False(File.Exists(Path.Combine(_directory, "age-49h.json")));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void A_non_positive_retention_falls_back_to_36_hours(int hours)
        {
            Assert.Equal(TimeSpan.FromHours(36),
                new ResponseSpillWriter(_directory, TimeSpan.FromHours(hours)).Retention);
        }

        [Fact]
        public void Every_new_spill_runs_cleanup_and_keeps_its_returned_path()
        {
            var now = DateTime.UtcNow;
            WriteAged("expired.json", now, 40);
            WriteAged("fresh.json", now, 1);

            var spill = new ResponseSpillWriter(_directory).Write(
                "export_shared_parameter_file",
                new JObject { ["ok"] = true },
                ResponseSpillFormat.Json);

            Assert.True(File.Exists(spill.Path));
            Assert.False(File.Exists(Path.Combine(_directory, "expired.json")));
            Assert.True(File.Exists(Path.Combine(_directory, "fresh.json")));
        }

        [Fact]
        public void Cleanup_never_deletes_the_new_artifact_when_existing_timestamps_are_in_the_future()
        {
            Directory.CreateDirectory(_directory);
            for (var i = 0; i < 60; i++)
            {
                var path = Path.Combine(_directory, "future-" + i + ".json");
                File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(1).AddMinutes(i));
            }

            var spill = new ResponseSpillWriter(_directory).Write(
                "export_shared_parameter_file",
                new JObject { ["ok"] = true },
                ResponseSpillFormat.Json);

            Assert.True(File.Exists(spill.Path));
            Assert.Equal(61, Directory.GetFiles(_directory).Length);
        }

        [Fact]
        public void SQLite_schema_is_bounded_so_even_extreme_dynamic_columns_keep_envelope_under_budget()
        {
            var row = new JObject();
            for (var i = 0; i < 1000; i++)
                row["column_" + i + "_" + new string('x', 800)] = i;
            var payload = new JObject { ["records"] = new JArray(row) };

            var spill = new ResponseSpillWriter(_directory).Write("get_material_takeoff", payload, ResponseSpillFormat.Sqlite);

            Assert.True(spill.Envelope.Value<bool?>("schema_truncated") ?? false);
            Assert.True(Encoding.UTF8.GetByteCount(spill.Envelope.ToString(Newtonsoft.Json.Formatting.None)) < ResponseSizeGuard.EnforcementBudgetBytes);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, true);
            }
            catch { }
        }
    }
}
