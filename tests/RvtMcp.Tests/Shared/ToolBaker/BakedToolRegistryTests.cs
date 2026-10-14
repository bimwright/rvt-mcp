using System;
using System.IO;
using System.Linq;
using RvtMcp.Plugin.ToolBaker;
using RvtMcp.Server.Bake;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class BakedToolRegistryTests
    {
        [Fact]
        public void GetAllSortedForList_orders_by_recent_usage_score()
        {
            using var sandbox = new TempDir();
            var bakedDir = Path.Combine(sandbox.Path, "baked");
            using var db = new BakeDb(Path.Combine(sandbox.Path, "bake.db"));
            db.Migrate();
            Assert.True(db.TryInsertRegistryRecord(Record(
                "old_tool",
                usageCount: 10,
                history: @"[{""event"":""run_succeeded"",""at"":""" + DateTimeOffset.UtcNow.AddDays(-40).ToString("o") + @"""}]")));
            Assert.True(db.TryInsertRegistryRecord(Record(
                "recent_tool",
                usageCount: 1,
                history: @"[{""event"":""run_succeeded"",""at"":""" + DateTimeOffset.UtcNow.ToString("o") + @"""}]")));

            var registry = new BakedToolRegistry(bakedDir);

            var tools = registry.GetAllSortedForList().ToArray();
            Assert.Equal(new[] { "recent_tool", "old_tool" }, tools.Select(t => t.Name).ToArray());
            Assert.Equal(1, tools[0].UsageScore30d);
            Assert.Equal(0, tools[1].UsageScore30d);
        }

        [Fact]
        public void LoadBakeDb_tolerates_pre_task9_registry_schema()
        {
            using var sandbox = new TempDir();
            CreateTask8Registry(Path.Combine(sandbox.Path, "bake.db"));

            var registry = new BakedToolRegistry(Path.Combine(sandbox.Path, "baked"));

            var tool = Assert.Single(registry.GetAllSortedForList());
            Assert.Equal("task8_tool", tool.Name);
            Assert.Equal(0, tool.UsageCount);
            Assert.Equal("accepted", tool.LifecycleState);
        }

        [Fact]
        public void GetAllSortedForList_refreshes_after_external_registry_run_update()
        {
            using var sandbox = new TempDir();
            var bakedDir = Path.Combine(sandbox.Path, "baked");
            using var db = new BakeDb(Path.Combine(sandbox.Path, "bake.db"));
            db.Migrate();
            Assert.True(db.TryInsertRegistryRecord(Record("shared_tool", usageCount: 0, history: "[]")));
            var registry = new BakedToolRegistry(bakedDir);

            Assert.True(db.TryRecordRegistryRun("shared_tool", "2027", success: true, error: null, DateTimeOffset.UtcNow));

            var tool = Assert.Single(registry.GetAllSortedForList());
            var tested = (JObject)JObject.Parse(tool.CompatMap)["tested"];
            Assert.True(tested["2027"].Value<bool>("ok"));
            Assert.Equal(1, tool.UsageCount);
        }

        private static BakedToolRecord Record(string name, int usageCount, string history)
        {
            return new BakedToolRecord
            {
                Name = name,
                Description = name,
                Source = "send_code",
                ParamsSchema = "{}",
                CompatMap = @"{""origin"":""2026"",""tested"":{}}",
                ReviewedByUser = true,
                CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                UsageCount = usageCount,
                FailureRate = 0,
                VersionHistoryBlob = history,
                LifecycleState = "accepted"
            };
        }

        private static void CreateTask8Registry(string dbPath)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE registry(
    name TEXT PRIMARY KEY,
    description TEXT NOT NULL,
    source TEXT NOT NULL,
    params_schema TEXT NOT NULL,
    compat_map TEXT NOT NULL,
    dll_bytes BLOB NULL,
    source_code TEXT NULL,
    created_from_suggestion_id TEXT NULL,
    reviewed_by_user INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    last_used_at TEXT NULL,
    failure_rate REAL NOT NULL,
    version_history_blob TEXT NOT NULL
);
INSERT INTO registry(
    name, description, source, params_schema, compat_map, source_code,
    reviewed_by_user, created_at, last_used_at, failure_rate, version_history_blob
) VALUES (
    'task8_tool', 'Task 8', 'send_code', '{}', '{""origin"":""2026"",""tested"":{}}', 'source',
    1, '2026-04-27T00:00:00Z', NULL, 0, '[]'
);";
            command.ExecuteNonQuery();
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bimwright-registry-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
        }
    }
}
