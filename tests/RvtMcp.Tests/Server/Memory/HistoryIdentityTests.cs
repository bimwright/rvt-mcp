using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RvtMcp.Plugin;
using Newtonsoft.Json.Linq;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    public class HistoryIdentityTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-identity-" + Guid.NewGuid().ToString("N"));
        private ChangeHistoryStore Store => new ChangeHistoryStore(_root);
        private static JObject Capture(string path, string physical, string lineage = "same-origin")
        {
            var payload = ChangeHistoryTests.Capture(path);
            foreach (var model in new[] { (JObject)payload["activeModel"], (JObject)payload["documents"][0]["model"] })
            { model["physicalKey"] = physical; model["lineageKey"] = lineage; }
            return payload;
        }
        private static JObject Model(JObject payload) => (JObject)payload["activeModel"];
        private static string Key(JObject payload) => Model(payload).Value<string>("key");

        [Fact]
        public void Rename_resolves_original_history_without_creating_a_second_database()
        {
            var store = Store; var first = Capture(@"C:\fixture\before.rvt", "physical-one"); store.Ingest(first);
            var renamed = Capture(@"C:\fixture\after.rvt", "physical-one");
            var before = Directory.GetFiles(_root, "*.db", SearchOption.AllDirectories);
            var result = store.Query(Key(renamed), model: Model(renamed));
            Assert.Single(result["calls"]); Assert.Equal("same_file", result["identity"].Value<string>("status"));
            Assert.Equal(Key(first), result["calls"][0].Value<string>("modelKey"));
            // Read-only SQLite can create WAL coordination sidecars; no model/catalog
            // database or change record is created by this explicit history query.
            Assert.Equal(before, Directory.GetFiles(_root, "*.db", SearchOption.AllDirectories));
        }

        [Fact]
        public void Copy_requires_a_choice_and_continuation_is_reversible_without_moving_calls()
        {
            var store = Store; var first = Capture(@"C:\fixture\one.rvt", "physical-one"); store.Ingest(first);
            var copy = Capture(@"C:\fixture\two.rvt", "physical-two"); store.Ingest(copy);
            var pending = store.Query(Key(copy), model: Model(copy));
            Assert.Single(pending["calls"]); Assert.Equal("needs_choice", pending["identity"].Value<string>("status"));
            store.ResolveIdentity(Model(copy), Key(first), "continue", "Owner continues the same model after Save As.");
            Assert.Equal(2, Store.Query(Key(copy))["calls"].Count());
            store.ResolveIdentity(Model(copy), Key(first), "separate", "Owner chooses an independent copy.");
            Assert.Single(Store.Query(Key(copy))["calls"]); Assert.Single(Store.Query(Key(first))["calls"]);
        }

        [Fact]
        public void Empty_history_read_does_not_create_the_catalog()
        {
            var payload = Capture(@"C:\fixture\new.rvt", "new-file");
            Assert.Empty(Store.Query(Key(payload), model: Model(payload))["calls"]);
            Assert.False(Directory.Exists(_root));
        }

        [Fact]
        public void Windows_file_identity_survives_rename_and_hardlink_but_distinguishes_a_copy()
        {
            Directory.CreateDirectory(_root);
            var original = Path.Combine(_root, "one.rvt"); var renamed = Path.Combine(_root, "renamed.rvt");
            var copied = Path.Combine(_root, "copy.rvt"); var alias = Path.Combine(_root, "alias.rvt");
            File.WriteAllText(original, "physical identity fixture");
            var key = HistoryFileIdentity.TryGet(original); Assert.NotNull(key);
            File.Move(original, renamed); Assert.Equal(key, HistoryFileIdentity.TryGet(renamed));
            File.Copy(renamed, copied); Assert.NotEqual(key, HistoryFileIdentity.TryGet(copied));
            Assert.True(CreateHardLink(alias, renamed, IntPtr.Zero));
            Assert.Equal(key, HistoryFileIdentity.TryGet(alias));
            Assert.Null(HistoryFileIdentity.TryGet(original));
        }

        [Fact]
        public void Continuation_cycles_are_rejected_and_existing_decision_survives()
        {
            var store = Store; var a = Capture(@"C:\fixture\a.rvt", "a"); var b = Capture(@"C:\fixture\b.rvt", "b");
            store.Ingest(a); store.Ingest(b);
            store.ResolveIdentity(Model(b), Key(a), "continue", "Confirmed continuation.");
            Assert.Throws<ArgumentException>(() => store.ResolveIdentity(Model(a), Key(b), "continue", "Would create a cycle."));
            Assert.Equal(2, Store.Query(Key(b))["calls"].Count());
        }

        [Fact]
        public void Linked_calls_keep_reason_ownership_and_an_explicit_separate_choice_stays_resolved()
        {
            var store = Store; var a = Capture(@"C:\fixture\a.rvt", "a"); var b = Capture(@"C:\fixture\b.rvt", "b");
            store.Ingest(a); store.Ingest(b);
            store.ResolveIdentity(Model(b), Key(a), "continue", "Confirmed continuation.");
            store.Record(Key(a), new[] { a.Value<string>("callId") }, new JObject { ["reason"] = "Original reason" });
            var rows = store.Query(Key(b))["calls"];
            Assert.Equal("recorded", rows.Single(r => r.Value<string>("modelKey") == Key(a)).Value<string>("reasonStatus"));
            Assert.Equal("unassigned", rows.Single(r => r.Value<string>("modelKey") == Key(b)).Value<string>("reasonStatus"));
            store.ResolveIdentity(Model(b), Key(a), "separate", "Independent copy.");
            Assert.Equal("separate", Store.Query(Key(b), model: Model(b))["identity"].Value<string>("status"));
        }

        [Fact]
        public void Legacy_databases_can_be_listed_and_linked_without_prior_catalog_metadata()
        {
            var store = Store; var a = Capture(@"C:\fixture\legacy.rvt", "a"); store.Ingest(a);
            File.Delete(Path.Combine(_root, "projects", ".history-identities.db"));
            var b = Capture(@"C:\fixture\continued.rvt", "b");
            Assert.Equal(Key(a), store.ListModels()["models"][0].Value<string>("modelKey"));
            Assert.False(File.Exists(Path.Combine(_root, "projects", ".history-identities.db")));
            store.ResolveIdentity(Model(b), Key(a), "continue", "Owner links historical file explicitly.");
            Assert.Single(Store.Query(Key(b))["calls"]);
        }

        [Fact]
        public void Recording_again_through_the_original_alias_does_not_create_a_self_link()
        {
            var store = Store; var a = Capture(@"C:\fixture\a.rvt", "one-file"); var b = Capture(@"C:\fixture\alias.rvt", "one-file");
            store.Ingest(a); store.Ingest(b); store.Ingest(Capture(@"C:\fixture\a.rvt", "one-file"));
            Assert.Equal(3, Store.Query(Key(a))["calls"].Count());
            Assert.Equal(3, Store.Query(Key(b))["calls"].Count());
        }

        [Fact]
        public async System.Threading.Tasks.Task Concurrent_alias_observation_converges_without_lost_calls()
        {
            var captures = Enumerable.Range(0, 8).Select(i => Capture(@"C:\fixture\alias" + i + ".rvt", "one-file")).ToArray();
            await System.Threading.Tasks.Task.WhenAll(captures.Select(c => System.Threading.Tasks.Task.Run(() => Store.Ingest(c))));
            foreach (var capture in captures) Assert.Equal(8, Store.Query(Key(capture))["calls"].Count());
        }

        [Fact]
        public void Linked_queries_apply_global_order_limit_dates_and_element_filters()
        {
            var store = Store; var a = Capture(@"C:\fixture\a.rvt", "a"); var b = Capture(@"C:\fixture\b.rvt", "b");
            var c = Capture(@"C:\fixture\a.rvt", "a");
            a["at"] = "2026-10-01T01:00:00Z"; b["at"] = "2026-10-01T03:00:00Z"; c["at"] = "2026-10-01T02:00:00Z";
            b["documents"][0]["elements"][0]["elementId"] = 2;
            store.Ingest(a); store.Ingest(b); store.Ingest(c);
            store.ResolveIdentity(Model(b), Key(a), "continue", "Owner confirms continuity.");
            var page = store.Query(Key(b), limit: 2);
            Assert.Equal(new[] { b.Value<string>("callId"), c.Value<string>("callId") }, page["calls"].Select(x => x.Value<string>("callId")));
            Assert.True(page.Value<bool>("hasMore"));
            var older = store.Query(Key(b), until: "2026-10-01T01:30:00Z");
            Assert.Equal(a.Value<string>("callId"), Assert.Single(older["calls"]).Value<string>("callId"));
            Assert.False(older.Value<bool>("hasMore"));
            Assert.Single(store.Query(Key(b), elementId: 2)["calls"]);
            Assert.DoesNotContain(@"C:\fixture", page.ToString());
            Assert.DoesNotContain("physicalKey", page.ToString());
        }

        [Fact]
        public void Model_listing_paginates_and_titles_do_not_suggest_continuation()
        {
            var store = Store; var a = Capture(@"C:\fixture\a.rvt", "a", "origin-a"); var b = Capture(@"C:\fixture\b.rvt", "b", "origin-b");
            store.Ingest(a); store.Ingest(b);
            Assert.Equal("known", store.Query(Key(b))["identity"].Value<string>("status"));
            var page = store.ListModels(limit: 1); var next = store.ListModels(page.Value<string>("nextAfterModelKey"), 1);
            Assert.Single(page["models"]); Assert.Single(next["models"]);
            Assert.NotEqual(page["models"][0].Value<string>("modelKey"), next["models"][0].Value<string>("modelKey"));
            Assert.Null(next.Value<string>("nextAfterModelKey"));
        }

        [Fact]
        public void Unknown_source_and_separating_proven_file_aliases_are_rejected()
        {
            var store = Store; var a = Capture(@"C:\fixture\a.rvt", "one-file"); var b = Capture(@"C:\fixture\b.rvt", "one-file");
            Assert.Throws<ArgumentException>(() => store.ResolveIdentity(Model(b), Key(a), "continue", "Not recorded."));
            Assert.False(Directory.Exists(_root));
            store.Ingest(a); store.Ingest(b);
            Assert.Throws<ArgumentException>(() => store.ResolveIdentity(Model(b), Key(a), "separate", "Invalid alias split."));
            Assert.Equal(2, Store.Query(Key(b))["calls"].Count());
            Assert.Equal("same_file", Store.Query(Key(b))["identity"].Value<string>("status"));
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
