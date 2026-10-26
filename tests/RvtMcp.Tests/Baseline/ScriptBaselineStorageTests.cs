using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using RvtMcp.Plugin.ToolBaker;
using RvtMcp.Server.Bake;
using Xunit;

namespace RvtMcp.Tests
{
    public class ScriptBaselineStorageTests
    {
        [Fact]
        public void Migration_preserves_populated_version_one_registry_suggestions_and_usage()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-script-baseline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "bake.db");
                var dll = ScriptBaselineFixture.CreateLegacyDatabase(path);
                using (var pe = new PEReader(new MemoryStream(dll))) Assert.True(pe.HasMetadata);
                using (var connection = ScriptBaselineFixture.Open(path))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT version FROM schema_version";
                    Assert.Equal(1L, command.ExecuteScalar());
                }
                var before = Snapshot(path);

                using var db = new BakeDb(path);
                db.Migrate();
                db.Migrate();

                Assert.Equal(before, Snapshot(path));
                var records = db.ReadRegistryRecords();
                Assert.Equal(5, records.Count);
                Assert.Equal(new[] { "legacy_pre_v0.3", "macro", "preset", "send_code" },
                    records.Select(r => r.Source).Distinct().OrderBy(s => s, StringComparer.Ordinal));
                Assert.Equal(dll, records.Single(r => r.Name == "baseline_compiled").DllBytes);
                var archived = records.Single(r => r.Name == "baseline_archived");
                Assert.Equal("archived", archived.LifecycleState);
                Assert.Equal(dll, archived.DllBytes);
                foreach (var record in records.Where(r => r.Source == "preset" || r.Source == "macro"))
                {
                    Assert.True(BakedToolRuntimeSource.TryParse(record.SourceCode, out var runtime));
                    Assert.Equal(record.Source, runtime.Kind);
                    if (runtime.Kind == "preset") Assert.Equal("get_current_view_info", runtime.Tool);
                    else Assert.Equal(new[] { "get_current_view_info", "get_selection" }, runtime.Sequence);
                }
                Assert.Equal(new[] { "accepted", "never", "open", "snoozed" },
                    db.ListSuggestions().Select(s => s.State).OrderBy(s => s, StringComparer.Ordinal));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        private static string Snapshot(string path)
        {
            var tables = new List<object>();
            using var connection = ScriptBaselineFixture.Open(path);
            foreach (var table in new[] { "registry", "suggestions", "usage_events" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM " + table + " ORDER BY 1";
                using var reader = command.ExecuteReader();
                var rows = new List<object[]>();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Add(values);
                }
                tables.Add(rows);
            }
            return JsonConvert.SerializeObject(tables);
        }
    }
}
