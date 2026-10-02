using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Survey;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    public class SurveySnapshotTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-survey-history-" + Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        private static string Id() => Guid.NewGuid().ToString("N");
        private static JArray Targets() => new JArray(new JObject { ["elementId"] = 1, ["uniqueId"] = "unique-1" });
        private static JObject Snapshot()
        {
            JObject Values(string text) => new JObject { ["status"] = "captured", ["parameters"] = new JArray(new JObject {
                ["builtInId"] = -1010106, ["ownerId"] = 1, ["storageType"] = "String", ["status"] = "read", ["value"] = text }) };
            return new JObject { ["status"] = "captured", ["surveyId"] = Id(), ["source"] = "plugin_execution_readback",
                ["beforeAt"] = "2026-10-02T00:00:01Z", ["afterAt"] = "2026-10-02T00:00:02Z", ["elements"] = new JArray(new JObject {
                    ["elementId"] = 1, ["uniqueId"] = "unique-1", ["before"] = Values("old"), ["after"] = Values("new") }) };
        }

        [Fact]
        public void Selection_is_session_bound_defensively_copied_and_single_use()
        {
            var cache = new SurveySnapshotCache(); var doc = new object(); var session = Id(); var targets = Targets();
            cache.Remember(session, doc, "model", Id(), targets, _now);
            targets[0]["uniqueId"] = "forged";
            Assert.Equal("unavailable", cache.Take(Id(), doc, "model", _now).Value<string>("status"));
            var selection = cache.Take(session, doc, "model", _now);
            Assert.Equal("unique-1", selection["targets"][0].Value<string>("uniqueId"));
            Assert.Equal("unavailable", cache.Take(session, doc, "model", _now).Value<string>("status"));
            Assert.False(Directory.Exists(_root));
        }

        [Theory]
        [InlineData("reopen")]
        [InlineData("save-as")]
        [InlineData("expired")]
        [InlineData("clock-back")]
        public void Context_or_time_changes_discard_selection(string change)
        {
            var cache = new SurveySnapshotCache(); var doc = new object(); var session = Id();
            cache.Remember(session, doc, "model", Id(), Targets(), _now);
            var result = cache.Take(session, change == "reopen" ? new object() : doc, change == "save-as" ? "copy" : "model",
                change == "expired" ? _now.AddMinutes(6) : change == "clock-back" ? _now.AddSeconds(-1) : _now);
            Assert.Equal("unavailable", result.Value<string>("status"));
            Assert.Null(result["targets"]);
            Assert.Equal("unavailable", cache.Take(session, doc, "model", _now).Value<string>("status"));
        }

        [Theory]
        [InlineData("document_changed_or_undo")]
        [InlineData("document_or_view_context_changed")]
        [InlineData("history_disabled")]
        public void Invalidation_discards_all_sessions_without_retaining_values(string reason)
        {
            var cache = new SurveySnapshotCache(); var doc = new object(); var sessions = new[] { Id(), Id() };
            foreach (var session in sessions) cache.Remember(session, doc, "model", Id(), Targets(), _now);
            cache.Clear(reason);
            foreach (var session in sessions) Assert.Equal(reason, cache.Take(session, doc, "model", _now).Value<string>("reason"));
        }

        [Fact]
        public void Capacity_and_latest_survey_are_bounded()
        {
            var cache = new SurveySnapshotCache(); var doc = new object(); var sessions = Enumerable.Range(0,9).Select(_ => Id()).ToArray();
            for (var i = 0; i < sessions.Length; i++) cache.Remember(sessions[i], doc, "model", Id(), Targets(), _now.AddSeconds(i));
            Assert.Equal("unavailable", cache.Take(sessions[0], doc, "model", _now.AddSeconds(10)).Value<string>("status"));
            var latest = Id(); cache.Remember(sessions[1], doc, "model", latest, Targets(), _now.AddSeconds(10));
            Assert.Equal(latest, cache.Take(sessions[1], doc, "model", _now.AddSeconds(11)).Value<string>("surveyId"));
            Assert.Throws<ArgumentException>(() => cache.Remember(Id(), doc, "", Id(), Targets(), _now));
            Assert.Throws<ArgumentException>(() => cache.Remember(Id(), doc, "model", Id(), new JArray(Enumerable.Range(0,26)), _now));
        }

        [Theory]
        [InlineData("modified", "unique-1", true)]
        [InlineData("modified", "replacement", false)]
        [InlineData("added", "unique-1", false)]
        [InlineData("deleted", "unique-1", false)]
        [InlineData("modified", null, false)]
        public void Observations_only_match_captured_modified_unique_identity(string kind, string unique, bool matches)
        {
            var element = new JObject { ["elementId"] = 1, ["uniqueId"] = unique, ["kind"] = kind };
            Assert.Equal(matches, SurveySnapshotCache.Match(Snapshot(), element) != null);
            Assert.Null(SurveySnapshotCache.Match(SurveySnapshotCache.Unavailable("stale"), element));
        }

        [Fact]
        public void Survey_values_persist_beside_direct_setter_values_and_survive_reopen()
        {
            var capture = ChangeHistoryTests.Capture(); var snapshot = Snapshot();
            capture["documents"][0]["parameterSnapshot"] = snapshot;
            capture["parameterValues"] = new JObject { ["parameter"] = "Comments", ["updated"] = new JArray(new JObject {
                ["elementId"] = 1, ["oldValue"] = "direct-old", ["newValue"] = "direct-new" }) };
            var store = new ChangeHistoryStore(_root); store.Ingest(capture);
            var key = capture["activeModel"].Value<string>("key");
            var call = Assert.Single(new ChangeHistoryStore(_root).Query(key)["calls"]);
            Assert.Equal(1, call["parameterSnapshot"].Value<int>("observedElementCount"));
            Assert.Null(call["parameterSnapshot"]["elements"]);
            var element = Assert.Single(call["elements"]);
            Assert.Equal("direct-old", element["before"].Value<string>("value"));
            Assert.Equal("old", element["before"]["surveySnapshot"]["parameters"][0].Value<string>("value"));
            Assert.Equal("new", element["after"]["surveySnapshot"]["parameters"][0].Value<string>("value"));
            Assert.Equal(snapshot.Value<string>("surveyId"), element["before"]["surveySnapshot"].Value<string>("surveyId"));
            store.Ingest(capture); Assert.Single(store.Query(key)["calls"]);
            snapshot["elements"][0]["before"]["parameters"][0]["value"] = "different";
            Assert.Throws<InvalidOperationException>(() => store.Ingest(capture));
        }

        [Fact]
        public void Unavailable_snapshot_remains_explicit_and_unmeasured_values_stay_null()
        {
            var capture = ChangeHistoryTests.Capture();
            capture["documents"][0]["parameterSnapshot"] = SurveySnapshotCache.Unavailable("document_changed_or_undo");
            var store = new ChangeHistoryStore(_root); store.Ingest(capture);
            var call = Assert.Single(store.Query(capture["activeModel"].Value<string>("key"))["calls"]);
            Assert.Equal("document_changed_or_undo", call["parameterSnapshot"].Value<string>("reason"));
            Assert.Equal(JTokenType.Null, Assert.Single(call["elements"])["before"].Type);
            Assert.Equal(JTokenType.Null, Assert.Single(call["elements"])["after"].Type);
        }

        [Fact]
        public void Unmatched_and_deleted_targets_do_not_gain_before_values_from_survey()
        {
            var capture = ChangeHistoryTests.Capture(count:3);
            capture["documents"][0]["parameterSnapshot"] = Snapshot();
            capture["documents"][0]["elements"][0]["kind"] = "deleted";
            var store = new ChangeHistoryStore(_root); store.Ingest(capture);
            Assert.All(Assert.Single(store.Query(capture["activeModel"].Value<string>("key"))["calls"])["elements"], e => Assert.Equal(JTokenType.Null, e["before"].Type));
        }

        [Fact]
        public void Reason_context_cannot_supply_authoritative_snapshot_values()
        {
            var capture = ChangeHistoryTests.Capture(); var store = new ChangeHistoryStore(_root); store.Ingest(capture);
            var key = capture["activeModel"].Value<string>("key");
            store.Record(key, new[] { capture.Value<string>("callId") }, new JObject { ["survey"] = Snapshot() });
            var call = Assert.Single(store.Query(key)["calls"]);
            Assert.Null(call["parameterSnapshot"]);
            Assert.Equal(JTokenType.Null, Assert.Single(call["elements"])["before"].Type);
        }

        [Fact]
        public void Legacy_database_queries_do_not_create_snapshot_table()
        {
            var capture = ChangeHistoryTests.Capture(); var store = new ChangeHistoryStore(_root); store.Ingest(capture);
            var key = capture["activeModel"].Value<string>("key");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root,"projects",key+".db"), Pooling=false }.ToString());
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE change_call_snapshots"; cmd.ExecuteNonQuery();
            Assert.Single(store.Query(key)["calls"]);
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='change_call_snapshots'";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }

        [Fact]
        public void Survey_metadata_without_changes_never_publishes_pending_file()
        {
            var capture = ChangeHistoryTests.Capture(); capture["documents"] = new JArray(); capture["parameterSnapshot"] = Snapshot();
            Assert.Null(new ChangeHistoryTransfer(_root).Publish(capture.Value<string>("callId"), capture));
            Assert.False(Directory.Exists(_root));
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root,true); }
    }
}
