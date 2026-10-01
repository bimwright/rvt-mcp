using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    public class HistoryReviewRegressionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-history-review-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Read_publication_does_no_io_and_returns_no_marker()
        {
            var transfer = new ChangeHistoryTransfer(_root);
            var payload = ChangeHistoryTests.Capture(); payload["documents"] = new JArray();
            Assert.Null(transfer.Publish(payload.Value<string>("callId"), payload));
            Assert.False(Directory.Exists(_root));
        }

        [Fact]
        public void Unresolved_publication_reports_skip_without_writing_a_file()
        {
            var transfer = new ChangeHistoryTransfer(_root);
            var payload = ChangeHistoryTests.Capture(); payload["documents"][0]["model"] = null;
            var marker = transfer.Publish(payload.Value<string>("callId"), payload);
            Assert.Equal("skipped_identity", marker.Value<string>("status"));
            Assert.Equal(1, marker.Value<int>("skippedDocuments"));
            Assert.False(Directory.Exists(_root));
        }

        [Fact]
        public void Mixed_identity_persists_known_document_and_reports_skip_without_retaining_pending()
        {
            var store = new ChangeHistoryStore(_root);
            var payload = ChangeHistoryTests.Capture();
            var unknown = (JObject)payload["documents"][0].DeepClone(); unknown["model"] = null;
            ((JArray)payload["documents"]).Add(unknown);
            var id = payload.Value<string>("callId");
            Assert.Equal(id, store.Transfer.Publish(id, payload).Value<string>("id"));
            Assert.Single(store.Transfer.Read(id)["documents"]);
            var receipt = store.Consume(id);
            Assert.Equal("partial", receipt.Value<string>("status"));
            Assert.Equal(1, receipt.Value<int>("skippedDocuments"));
            Assert.Single(store.Query(payload["activeModel"].Value<string>("key"))["calls"]);
            Assert.Empty(store.Transfer.PendingIds());
        }

        [Fact]
        public void Recovery_honors_batch_limit_and_cancellation()
        {
            var store = new ChangeHistoryStore(_root);
            for (int i = 0; i < 3; i++) { var p = ChangeHistoryTests.Capture(); store.Transfer.Write(p.Value<string>("callId"), p); }
            store.RecoverPending(new CancellationToken(true)); Assert.Equal(3, store.Transfer.PendingIds().Length);
            store.RecoverPending(maxFiles: 1); Assert.Equal(2, store.Transfer.PendingIds().Length);
        }

        [Fact]
        public void Recovery_does_not_steal_a_live_gateways_receipt()
        {
            var store = new ChangeHistoryStore(_root); var p = ChangeHistoryTests.Capture();
            var id = p.Value<string>("callId"); store.Transfer.Write(id, p);
            using (var lease = new HistoryCallLease(id))
            { store.RecoverPending(); Assert.Single(store.Transfer.PendingIds()); }
            store.RecoverPending(); Assert.Empty(store.Transfer.PendingIds());
        }

        [Fact]
        public void Expired_capture_is_quarantined_without_database_ingestion()
        {
            var store = new ChangeHistoryStore(_root); var p = ChangeHistoryTests.Capture();
            var id = p.Value<string>("callId"); store.Transfer.Write(id, p);
            File.SetLastWriteTimeUtc(Path.Combine(store.Transfer.Root, id + ".json"), DateTime.UtcNow.AddDays(-8));
            store.RecoverPending(); Assert.Empty(store.Transfer.PendingIds());
            Assert.False(Directory.Exists(Path.Combine(_root, "projects")));
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "history-quarantine")));
        }

        [Fact]
        public async Task Recovery_worker_start_does_not_wait_for_storage()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var service = new HistoryRecoveryService(token => { entered.Set(); release.Wait(token); });
            try
            {
                await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
                Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
                Assert.False(release.IsSet);
            }
            finally { release.Set(); await service.StopAsync(CancellationToken.None); }
        }

        [Fact]
        public void Diagnostics_include_type_and_call_id_without_private_exception_text()
        {
            var lines = new List<string>(); var id = Guid.NewGuid().ToString("N");
            HistoryDiagnostics.Report("test_diagnostic", new IOException(@"C:\secret\client.rvt token=hidden"), id, lines.Add);
            var line = Assert.Single(lines);
            Assert.Contains("IOException", line); Assert.Contains(id, line);
            Assert.DoesNotContain("secret", line); Assert.DoesNotContain("hidden", line);
            HistoryDiagnostics.Report("test_diagnostic", new IOException(), id, lines.Add);
            Assert.Single(lines);
        }

        [Fact]
        public void Server_defaults_cannot_relax_plugin_policy()
        {
            var resolved = new RvtMcpConfig { ReadOnly = true, EnableSendCode = false }
                .WithRuntimeOptions(new RvtMcpConfig().ToRuntimeOptions());
            Assert.True(resolved.ReadOnlyOrDefault);
            Assert.False(resolved.EnableSendCodeOrDefault);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Either_side_can_disable_send_code(bool plugin, bool server)
        {
            var resolved = new RvtMcpConfig { EnableSendCode = plugin }
                .WithRuntimeOptions(new RvtMcpConfig { EnableSendCode = server }.ToRuntimeOptions());
            Assert.False(resolved.EnableSendCodeOrDefault);
        }

        [Fact]
        public void Missing_identity_leaves_no_retryable_capture()
        {
            var store = new ChangeHistoryStore(_root);
            var payload = ChangeHistoryTests.Capture(); payload["documents"][0]["model"] = null;
            var id = payload.Value<string>("callId"); store.Transfer.Write(id, payload);
            Assert.Equal("partial", store.Consume(id).Value<string>("status"));
            Assert.Empty(store.Transfer.PendingIds());
            Assert.False(Directory.Exists(Path.Combine(_root, "projects")));
        }

        [Fact]
        public void Malformed_pending_capture_is_not_retried_forever()
        {
            var store = new ChangeHistoryStore(_root);
            Directory.CreateDirectory(store.Transfer.Root);
            File.WriteAllText(Path.Combine(store.Transfer.Root, Guid.NewGuid().ToString("N") + ".json"), "{bad json");
            store.RecoverPending(); store.RecoverPending();
            Assert.Empty(store.Transfer.PendingIds());
        }

        [Fact]
        public void Runtime_migration_copies_legacy_config_and_retries_after_interrupted_copy()
        {
            var source = Path.Combine(_root, "RvtMcp"); Directory.CreateDirectory(Path.Combine(source, "baked"));
            File.WriteAllText(Path.Combine(source, "rvtmcp.config.json"), "{\"readOnly\":true}");
            File.WriteAllText(Path.Combine(source, "baked", "a.json"), "a");
            File.WriteAllText(Path.Combine(source, "baked", "b.json"), "b");
            var files = Directory.GetFiles(Path.Combine(source, "baked")).OrderBy(x => x).ToArray();
            using (var held = new FileStream(files[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.ThrowsAny<IOException>(() => LegacyDataMigration.MigrateOnce(_root));
            LegacyDataMigration.MigrateOnce(_root);
            var target = Path.Combine(_root, "Bimwright", "rvt-mcp");
            Assert.Equal("{\"readOnly\":true}", File.ReadAllText(Path.Combine(target, "rvtmcp.config.json")));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(target, "baked")).Length);
            Assert.True(File.Exists(Path.Combine(source, "rvtmcp.config.json")));
        }

        [Fact]
        public void Migration_conflict_preserves_both_files_and_reports_controlled_startup_failure()
        {
            var source = Path.Combine(_root, "RvtMcp"); var target = Path.Combine(_root, "Bimwright", "rvt-mcp");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "rvtmcp.config.json"), "legacy");
            File.WriteAllText(Path.Combine(target, "rvtmcp.config.json"), "current");
            var log = new List<string>();
            Assert.False(LegacyDataMigration.TryMigrateOnce(log.Add, _root));
            Assert.Contains(log, line => line.Contains("MIGRATION_REQUIRED"));
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(source, "rvtmcp.config.json")));
            Assert.Equal("current", File.ReadAllText(Path.Combine(target, "rvtmcp.config.json")));
            Assert.False(File.Exists(Path.Combine(target, ".migrated-from-rvtmcp-v2")));
        }

        [Fact]
        public void Migration_uses_sqlite_backup_including_committed_WAL()
        {
            var source = Path.Combine(_root, "RvtMcp"); Directory.CreateDirectory(source);
            var db = Path.Combine(source, "bake.db");
            using var writer = new SqliteConnection("Data Source=" + db + ";Pooling=False"); writer.Open();
            using (var command = writer.CreateCommand())
            { command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE proof(value TEXT); INSERT INTO proof VALUES('committed in WAL');"; command.ExecuteNonQuery(); }
            Assert.True(new FileInfo(db + "-wal").Length > 0);
            LegacyDataMigration.MigrateOnce(_root);
            var target = Path.Combine(_root, "Bimwright", "rvt-mcp", "bake.db");
            using var reader = new SqliteConnection("Data Source=" + target + ";Mode=ReadOnly;Pooling=False"); reader.Open();
            using var query = reader.CreateCommand(); query.CommandText = "SELECT value FROM proof";
            Assert.Equal("committed in WAL", query.ExecuteScalar());
            LegacyDataMigration.MigrateOnce(_root); // Completed migration is idempotent.
            Assert.True(File.Exists(db));
        }

        [Fact]
        public void Migration_retry_after_database_snapshot_does_not_conflict_with_its_own_copy()
        {
            var source = Path.Combine(_root, "RvtMcp"); Directory.CreateDirectory(source);
            var db = Path.Combine(source, "bake.db");
            using (var writer = new SqliteConnection("Data Source=" + db + ";Pooling=False"))
            {
                writer.Open(); using var command = writer.CreateCommand();
                command.CommandText = "CREATE TABLE proof(value TEXT); INSERT INTO proof VALUES('data');"; command.ExecuteNonQuery();
            }
            var config = Path.Combine(source, "rvtmcp.config.json"); File.WriteAllText(config, "{}");
            using (var held = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.ThrowsAny<IOException>(() => LegacyDataMigration.MigrateOnce(_root));
            Assert.True(File.Exists(Path.Combine(_root, "Bimwright", "rvt-mcp", "bake.db")));
            LegacyDataMigration.MigrateOnce(_root);
            Assert.True(File.Exists(Path.Combine(_root, "Bimwright", "rvt-mcp", ".migrated-from-rvtmcp-v2")));
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
