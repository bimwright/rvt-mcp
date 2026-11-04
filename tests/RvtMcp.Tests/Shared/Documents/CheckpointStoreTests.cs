using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class CheckpointStoreTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rvt-checkpoint-store-" + Guid.NewGuid().ToString("N"));
        private string ModelPath => Path.Combine(root, "Tower.rvt");
        private string Folder => Path.Combine(root, "Tower.checkpoints");
        private string Index => Path.Combine(Folder, "index.json");
        private CheckpointStore Store => new CheckpointStore(ModelPath);

        public CheckpointStoreTests()
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(ModelPath, "working model");
        }

        public void Dispose()
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(root, true);
        }

        private CheckpointEntry Entry(string id, string created, bool milestone = false, bool exists = true)
        {
            var entry = new CheckpointEntry
            {
                Id = id, File = id + ".rvt", Label = id, CreatedUtc = created, Milestone = milestone,
                Bytes = 8, Source = new CheckpointSource { NumberOfSaves = 2, VersionGuid = "11111111-1111-1111-1111-111111111111" },
                RevitYear = 2026, User = "test"
            };
            if (exists) File.WriteAllText(Path.Combine(Folder, entry.File), "snapshot");
            return entry;
        }

        private CheckpointEntry[] Seed()
        {
            Directory.CreateDirectory(Folder);
            var entries = new[]
            {
                Entry("20260101-000000", "2026-01-01T00:00:00Z"),
                Entry("20260201-000000", "2026-02-01T00:00:00Z"),
                Entry("20200101-000000", "2020-01-01T00:00:00Z", milestone: true),
                Entry("20260301-000000", "2026-03-01T00:00:00Z", exists: false)
            };
            File.WriteAllText(Path.Combine(Folder, "untracked.rvt"), "do not delete");
            CheckpointIndex.Update(Index, "Tower.rvt", data => data.Checkpoints.AddRange(entries));
            return entries;
        }

        [Fact]
        public void List_reports_newest_index_entries_missing_files_and_untracked_files()
        {
            var entries = Seed();
            var list = Store.List();
            Assert.Equal(entries[3].Id, list.Items[0].Entry.Id);
            Assert.False(list.Items[0].Exists);
            Assert.Equal(8, Assert.Single(list.Items, x => x.Entry.Id == entries[0].Id).Bytes);
            Assert.Equal(new[] { "untracked.rvt" }, list.UntrackedFiles);
        }

        [Fact]
        public void Changed_prune_plan_cannot_delete_anything()
        {
            var entries = Seed();
            var plan = Store.Plan(1);
            File.AppendAllText(Path.Combine(Folder, entries[0].File), "changed");
            var result = Store.Prune(1, plan.PlanHash);
            Assert.Equal("PLAN_CHANGED", result.Error);
            Assert.True(File.Exists(Path.Combine(Folder, entries[0].File)));
            Assert.Equal(4, CheckpointIndex.Load(Index, "Tower.rvt").Checkpoints.Count);
        }

        [Fact]
        public void Prune_deletes_only_approved_indexed_files_and_removes_missing_entries()
        {
            var entries = Seed();
            Assert.Equal("PLAN_HASH_REQUIRED", Store.Prune(1, null).Error);
            var plan = Store.Plan(1);
            var result = Store.Prune(1, plan.PlanHash);
            Assert.Null(result.Error);
            Assert.Equal(new[] { entries[0].Id }, result.DeletedIds);
            Assert.Equal(new[] { entries[3].Id }, result.MissingIds);
            Assert.False(File.Exists(Path.Combine(Folder, entries[0].File)));
            Assert.True(File.Exists(Path.Combine(Folder, entries[1].File)));
            Assert.True(File.Exists(Path.Combine(Folder, entries[2].File)));
            Assert.True(File.Exists(Path.Combine(Folder, "untracked.rvt")));
            Assert.Equal(2, CheckpointIndex.Load(Index, "Tower.rvt").Checkpoints.Count);
        }

        [Fact]
        public void Empty_list_and_prune_do_not_create_checkpoint_directory()
        {
            Assert.Empty(Store.List().Items);
            var plan = Store.Plan(5);
            Assert.Null(Store.Prune(5, plan.PlanHash).Error);
            Assert.False(Directory.Exists(Folder));
        }

        [Fact]
        public void Prune_keeps_index_entry_when_file_cannot_be_deleted()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var entries = Seed();
            var old = Path.Combine(Folder, entries[0].File);
            File.SetAttributes(old, File.GetAttributes(old) | FileAttributes.ReadOnly);
            var plan = Store.Plan(1);
            var result = Store.Prune(1, plan.PlanHash);
            Assert.Contains(entries[0].Id, result.FailedIds);
            Assert.True(File.Exists(old));
            Assert.Contains(CheckpointIndex.Load(Index, "Tower.rvt").Checkpoints, item => item.Id == entries[0].Id);
        }

        [Fact]
        public void Prune_reports_deleted_files_if_index_replacement_fails()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var entries = Seed();
            var plan = Store.Plan(1);
            CheckpointPruneResult result;
            using (var held = new FileStream(Index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                result = Store.Prune(1, plan.PlanHash);

            Assert.Equal("CHECKPOINT_INDEX_WRITE_FAILED", result.Error);
            Assert.Contains(entries[0].Id, result.DeletedIds);
            Assert.Empty(result.MissingIds);
            Assert.False(File.Exists(Path.Combine(Folder, entries[0].File)));
            Assert.Contains(CheckpointIndex.Load(Index, "Tower.rvt").Checkpoints, item => item.Id == entries[0].Id);
            Assert.Contains(CheckpointIndex.Load(Index, "Tower.rvt").Checkpoints, item => item.Id == entries[3].Id);
        }

        [Fact]
        public void Restore_creates_new_file_and_refuses_existing_target()
        {
            var entries = Seed();
            var result = Store.Restore(entries[0].Id);
            Assert.Null(result.Error);
            Assert.Equal(Path.Combine(root, "Tower-restored-20260101-000000.rvt"), result.Path);
            Assert.Equal("snapshot", File.ReadAllText(result.Path));
            Assert.Equal("working model", File.ReadAllText(ModelPath));
            File.WriteAllText(result.Path, "user file");
            Assert.Equal("RESTORE_TARGET_EXISTS", Store.Restore(entries[0].Id).Error);
            Assert.Equal("user file", File.ReadAllText(result.Path));
        }

        [Fact]
        public void Restore_refuses_a_directory_at_the_target_path()
        {
            var entries = Seed();
            var target = Path.Combine(root, "Tower-restored-20260101-000000.rvt");
            Directory.CreateDirectory(target);
            Assert.Equal("RESTORE_TARGET_EXISTS", Store.Restore(entries[0].Id).Error);
            Assert.True(Directory.Exists(target));
        }

        // The restore handler needs Revit, so its guidance is pinned in source (SPEC r7 §4.4, P4 F6):
        // re-verifying after revit_open_model in the same session needs expectedDocumentId.
        [Fact]
        public void Restore_next_steps_tell_the_agent_to_reverify_with_expectedDocumentId()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "shared", "Handlers", "Documents", "ManageCheckpointsHandler.cs"));
            Assert.Contains("revit_switch_target(verify=true, expectedDocumentId=<_target.document_id", source);
            Assert.Contains("DOCUMENT_CHANGED", source);
        }

        private static string RepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
        }
    }
}
