using System;
using System.IO;
using System.Linq;
using RvtMcp.Server;
using RvtMcp.Server.Bake;
using RvtMcp.Plugin.ToolBaker;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolBakerMigrationTests
    {
        [Fact]
        public void BakePaths_uses_specified_audit_filename()
        {
            var paths = new BakePaths(@"C:\local-appdata-test");

            Assert.Equal(
                Path.Combine(@"C:\local-appdata-test", "Bimwright", "rvt-mcp", "bake-audit.jsonl"),
                paths.AuditJsonl);
        }

        [Fact]
        public void Program_startup_initialization_migrates_and_imports_legacy_registry()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            Directory.CreateDirectory(paths.LegacyBakedDir);
            File.WriteAllText(Path.Combine(paths.LegacyBakedDir, "startup_tool.cs"), "public class StartupTool {}");
            File.WriteAllText(paths.LegacyRegistryJson, JsonConvert.SerializeObject(new[]
            {
                new { Name = "startup_tool", Description = "Startup imported", ParametersSchema = "{}", CreatedUtc = "2026-04-26T00:00:00.0000000Z" }
            }));

            var result = Program.InitializeBakeStorage(paths);

            Assert.Equal(1, result.Imported);
            Assert.True(File.Exists(paths.BakeDb));
            Assert.True(File.Exists(paths.AuditJsonl));
            using var connection = OpenRead(paths.BakeDb);
            Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM registry WHERE name='startup_tool'"));
        }

        [Fact]
        public void Program_safe_startup_initialization_logs_warning_and_allows_continue_on_import_failure()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            Directory.CreateDirectory(paths.LegacyBakedDir);
            File.WriteAllText(paths.LegacyRegistryJson, "{ malformed legacy registry");
            var previousError = Console.Error;
            using var error = new StringWriter();
            Console.SetError(error);
            try
            {
                var continued = Program.TryInitializeBakeStorage(paths, out var result);

                Assert.False(continued);
                Assert.Null(result);
                Assert.Contains("Warning", error.ToString());
                Assert.Contains("ToolBaker bake storage initialization failed", error.ToString());
            }
            finally
            {
                Console.SetError(previousError);
            }
        }

        [Fact]
        public void ImportIfNeeded_imports_legacy_registry_without_deleting_legacy_files()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            Directory.CreateDirectory(paths.LegacyBakedDir);
            var sourcePath = Path.Combine(paths.LegacyBakedDir, "legacy_tool.cs");
            File.WriteAllText(sourcePath, "public class LegacyTool {}");
            File.WriteAllText(paths.LegacyRegistryJson, JsonConvert.SerializeObject(new[]
            {
                new
                {
                    Name = "legacy_tool",
                    Description = "Legacy imported tool",
                    ParametersSchema = @"{""type"":""object""}",
                    CreatedUtc = "2026-04-26T00:00:00.0000000Z",
                    CallCount = 4
                }
            }));

            using var db = new BakeDb(paths.BakeDb);
            db.Migrate();
            var importer = new LegacyBakedToolImporter(paths, db, new ToolBakerAuditLog(paths.AuditJsonl));

            var result = importer.ImportIfNeeded();

            Assert.Equal(1, result.Imported);
            Assert.True(File.Exists(paths.LegacyRegistryJson));
            Assert.True(File.Exists(sourcePath));

            using var connection = OpenRead(paths.BakeDb);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT description, source, params_schema, source_code, reviewed_by_user, created_at, version_history_blob FROM registry WHERE name='legacy_tool'";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Legacy imported tool", reader.GetString(0));
            Assert.Equal("legacy_pre_v0.3", reader.GetString(1));
            Assert.Equal(@"{""type"":""object""}", reader.GetString(2));
            Assert.Equal("public class LegacyTool {}", reader.GetString(3));
            Assert.Equal(1L, reader.GetInt64(4));
            Assert.Equal("2026-04-26T00:00:00.0000000Z", reader.GetString(5));
            Assert.Equal("legacy_imported", JObject.Parse(reader.GetString(6)).Value<string>("source_status"));
            Assert.Contains(ReadAuditEvents(paths.AuditJsonl), e => e.Value<string>("status") == "imported");
        }

        [Fact]
        public void ImportIfNeeded_is_idempotent_and_audits_duplicates_and_invalid_rows()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            Directory.CreateDirectory(paths.LegacyBakedDir);
            File.WriteAllText(Path.Combine(paths.LegacyBakedDir, "legacy_tool.cs"), "source");
            File.WriteAllText(paths.LegacyRegistryJson, JsonConvert.SerializeObject(new[]
            {
                new { Name = "legacy_tool", Description = "Legacy", ParametersSchema = "{}", CreatedUtc = "2026-04-26T00:00:00.0000000Z" },
                new { Name = "missing_source", Description = "Missing", ParametersSchema = "{}", CreatedUtc = "2026-04-26T00:00:00.0000000Z" },
                new { Name = "", Description = "Bad", ParametersSchema = "{}", CreatedUtc = "2026-04-26T00:00:00.0000000Z" }
            }));

            using var db = new BakeDb(paths.BakeDb);
            db.Migrate();
            var importer = new LegacyBakedToolImporter(paths, db, new ToolBakerAuditLog(paths.AuditJsonl));

            var first = importer.ImportIfNeeded();
            var second = importer.ImportIfNeeded();

            Assert.Equal(1, first.Imported);
            Assert.Equal(2, first.SkippedInvalid);
            Assert.Equal(0, second.Imported);
            Assert.Equal(1, second.SkippedDuplicate);

            using var connection = OpenRead(paths.BakeDb);
            Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM registry"));

            var statuses = ReadAuditEvents(paths.AuditJsonl)
                .Select(e => e.Value<string>("status"))
                .ToArray();
            Assert.Contains("imported", statuses);
            Assert.Contains("skipped-invalid", statuses);
            Assert.Contains("skipped-duplicate", statuses);
        }

        [Fact]
        public void BakedToolRegistry_Save_keeps_runtime_state_without_writing_legacy_json()
        {
            using var sandbox = new TempDir();
            var legacyDir = Path.Combine(sandbox.Path, "Bimwright", "baked");
            var registryPath = Path.Combine(legacyDir, "registry.json");
            var registry = new BakedToolRegistry(legacyDir);

            registry.Save(new BakedToolMeta
            {
                Name = "runtime_only",
                Description = "Runtime only",
                ParametersSchema = "{}",
                CreatedUtc = "2026-04-26T00:00:00.0000000Z",
            }, "source");

            Assert.Equal("runtime_only", registry.GetMeta("runtime_only")?.Name);
            Assert.Equal("source", registry.GetSource("runtime_only"));
            Assert.False(File.Exists(registryPath));
        }

        private static JObject[] ReadAuditEvents(string auditPath)
        {
            return File.ReadAllLines(auditPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(JObject.Parse)
                .ToArray();
        }

        private static SqliteConnection OpenRead(string dbPath)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        private static T Scalar<T>(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar(), typeof(T));
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bimwright-migration-test-" + Guid.NewGuid().ToString("N"));
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
