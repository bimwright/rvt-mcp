using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RvtMcp.Plugin;
using RvtMcp.Plugin.ToolBaker;
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolBakerHandlersTests
    {
        [Fact]
        public void ListBakeSuggestions_returns_user_facing_fields_only()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = ListBakeSuggestionsHandler.Handle(db);

            var suggestion = Assert.Single((JArray)JObject.Parse(json)["suggestions"]!);
            Assert.Equal("s1", (string)suggestion["id"]);
            Assert.Equal("Create Level", (string)suggestion["title"]);
            Assert.Equal("preset", (string)suggestion["source"]);
            Assert.Equal("open", (string)suggestion["state"]);
            Assert.Equal("mcp_only", suggestion["output_choices"]![0]!.Value<string>());
            Assert.Equal("ribbon_plus_mcp", suggestion["output_choices"]![1]!.Value<string>());
            Assert.NotNull(suggestion["score"]);
            Assert.NotNull(suggestion["created_at"]);
            Assert.Null(suggestion["payload_json"]);
        }

        [Fact]
        public void ListBakeSuggestions_filters_and_pages_with_hard_limit()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));
            db.UpsertSuggestion(Suggestion("s2", "preset:create_grid:start,end", "preset"));
            var dismissed = Suggestion("s3", "preset:create_room:x,y", "preset");
            dismissed.State = "never";
            db.UpsertSuggestion(dismissed);

            var json = ListBakeSuggestionsHandler.Handle(
                db,
                candidates: null,
                state: "open",
                startIndex: 0,
                limit: 1);
            var root = JObject.Parse(json);

            Assert.Equal(2, (int)root["count"]!);
            Assert.Equal(1, (int)root["returned_count"]!);
            Assert.True((bool)root["truncated"]!);
            Assert.Equal(1, (int)root["next_index"]!);
            Assert.Throws<ArgumentOutOfRangeException>(() => ListBakeSuggestionsHandler.Handle(
                db,
                candidates: null,
                state: "",
                startIndex: 0,
                limit: 501));
        }

        [Theory]
        [InlineData("snooze_30d", "snoozed")]
        [InlineData("never", "never")]
        public void DismissBakeSuggestion_updates_allowed_states(string action, string expectedState)
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = DismissBakeSuggestionHandler.Handle(db, "s1", action, DateTimeOffset.Parse("2026-04-27T00:00:00Z"));

            var root = JObject.Parse(json);
            Assert.True((bool)root["ok"]!);
            var stored = db.GetSuggestion("s1")!;
            Assert.Equal(expectedState, stored.State);
            if (action == "snooze_30d")
                Assert.Equal(DateTimeOffset.Parse("2026-05-27T00:00:00Z").ToString("o"), stored.SnoozeUntil);
        }

        [Fact]
        public void CreateBakeIssueDraft_returns_encoded_github_url_without_sensitive_material()
        {
            var suggestion = Suggestion("s1", "send_code:abc", "send_code");
            suggestion.PayloadJson = @"{
                ""tool"":""send_code_to_revit"",
                ""normalized_key"":""send_code:abc"",
                ""count"":9,
                ""first_seen_utc"":""2026-04-18T00:00:00Z"",
                ""last_seen_utc"":""2026-04-27T00:00:00Z"",
                ""code_cache_samples"":[""return doc.PathName + \""C:\\Users\\Admin\\Project Alpha.rvt\"" + \"" sk-testsecret12345 SensitiveWallType42\"";""],
                ""sample"":{""projectName"":""Project Alpha"",""url"":""https://example.com/client""}
            }";

            var url = CreateBakeIssueDraftHandler.Handle(suggestion, "2026");

            Assert.StartsWith("https://github.com/bimwright/rvt-mcp/issues/new?", url);
            Assert.Contains("title=MCP%20gap%20signal%3A%20recurring%20send_code_to_revit%20pattern", url);
            Assert.Contains("labels=mcp-gap", url);

            var body = GetQueryValue(url, "body");
            Assert.Contains("Source: `send_code_to_revit` cluster", body);
            Assert.Contains("Frequency: 9 over 10 days", body);
            Assert.Contains("Current Revit version: 2026", body);
            Assert.Contains("Redacted summary:", body);
            Assert.DoesNotContain(@"C:\Users\Admin", body);
            Assert.DoesNotContain("Project Alpha", body);
            Assert.DoesNotContain("https://example.com", body);
            Assert.DoesNotContain("sk-testsecret12345", body);
            Assert.DoesNotContain("SensitiveWallType42", body);
            Assert.DoesNotContain("return doc.PathName", body);
        }

        [Fact]
        public void DismissBakeSuggestion_send_code_gap_signal_returns_issue_url_and_audits_local_draft()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "send_code:abc", "send_code"));
            var auditPath = Path.Combine(sandbox.Path, "bake-audit.jsonl");
            var auditLog = new ToolBakerAuditLog(auditPath);

            var url = DismissBakeSuggestionHandler.Handle(
                db,
                "s1",
                "never_with_gap_signal",
                DateTimeOffset.Parse("2026-04-27T00:00:00Z"),
                "2026",
                auditLog);

            Assert.StartsWith("https://github.com/bimwright/rvt-mcp/issues/new?", url);

            var stored = db.GetSuggestion("s1")!;
            Assert.Equal("never", stored.State);
            Assert.Equal("gap_signal", stored.NeverReason);

            var audit = Assert.Single(File.ReadAllLines(auditPath).Select(ParseObjectNoDates));
            Assert.Equal("gap_issue_draft_created", (string)audit["event"]);
            Assert.Equal("s1", (string)audit["suggestion_id"]);
            Assert.Equal("2026-04-27T00:00:00.0000000+00:00", (string)audit["ts_utc"]);
        }

        [Fact]
        public void DismissBakeSuggestion_rejects_gap_signal_for_non_send_code_suggestions()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = DismissBakeSuggestionHandler.Handle(
                db,
                "s1",
                "never_with_gap_signal",
                DateTimeOffset.Parse("2026-04-27T00:00:00Z"),
                "2026");

            var root = JObject.Parse(json);
            Assert.False((bool)root["ok"]!);
            Assert.Equal("gap_signal_requires_send_code", (string)root["error_code"]);
            Assert.Equal("open", db.GetSuggestion("s1")!.State);
        }

        [Fact]
        public void AcceptBakeSuggestion_validates_name_and_records_failed_attempt_without_persisting_failed_state()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = AcceptBakeSuggestionHandler.Handle(db, "s1", "bad name", "mcp_only");

            var root = JObject.Parse(json);
            Assert.False((bool)root["ok"]!);
            Assert.Equal("invalid_name", (string)root["error_code"]);

            var stored = db.GetSuggestion("s1")!;
            Assert.Equal("open", stored.State);
            var attempts = (JArray)JObject.Parse(stored.PayloadJson)["accept_attempts"]!;
            Assert.Equal("invalid_name", (string)attempts[0]!["error_code"]);
            Assert.NotNull((string)attempts[0]!["attempted_at"]);
        }

        [Fact]
        public async Task AcceptBakeSuggestion_plugin_compile_failure_keeps_suggestion_open_and_does_not_insert_registry()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = await AcceptBakeSuggestionHandler.HandleAsync(
                db,
                "s1",
                "make_level",
                "mcp_only",
                pluginApply: request => Task.FromResult(new JObject
                {
                    ["success"] = false,
                    ["error_code"] = "compile_failed",
                    ["message"] = "Compilation failed",
                    ["diagnostics"] = new JObject { ["stage"] = "compile" }
                }));

            var root = JObject.Parse(json);
            Assert.False((bool)root["ok"]!);
            Assert.Equal("compile_failed", (string)root["error_code"]);
            Assert.False(db.ReadRegistryRecords().Any());
            Assert.Equal("open", db.GetSuggestion("s1")!.State);

            var attempts = (JArray)JObject.Parse(db.GetSuggestion("s1")!.PayloadJson)["accept_attempts"]!;
            Assert.Equal("compile_failed", (string)attempts[0]!["error_code"]);
        }

        [Fact]
        public async Task AcceptBakeSuggestion_pending_apply_keeps_receipt_and_operation_link_without_failure()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = await AcceptBakeSuggestionHandler.HandleAsync(
                db,
                "s1",
                "make_level",
                "mcp_only",
                pluginApply: request => Task.FromResult(new JObject
                {
                    ["success"] = false,
                    ["error"] = "REVIT_OPERATION_REQUIRES_USER",
                    ["_operation"] = new JObject
                    {
                        ["operationId"] = "op-pending-1",
                        ["execution"] = "timed_out_running",
                        ["outcome"] = "unknown",
                        ["complete"] = false,
                        ["mutationApplied"] = JValue.CreateNull()
                    }
                }));

            var root = JObject.Parse(json);
            Assert.False((bool)root["ok"]!);
            Assert.Equal("plugin_apply_pending", (string)root["error_code"]);
            Assert.Equal("op-pending-1", (string)root["operation_id"]);
            Assert.Equal("op-pending-1", (string)root["_operation"]!["operationId"]);
            Assert.False((bool)root["_operation"]!["complete"]!);

            var stored = db.GetSuggestion("s1")!;
            Assert.Equal("open", stored.State);
            var attempts = (JArray)JObject.Parse(stored.PayloadJson)["accept_attempts"]!;
            var attempt = Assert.Single(attempts);
            Assert.True((bool)attempt["pending"]!);
            Assert.Equal("op-pending-1", (string)attempt["operation_id"]);
            Assert.Null(attempt["error_code"]);
            Assert.False(db.ReadRegistryRecords().Any());
        }

        [Fact]
        public async Task AcceptBakeSuggestion_blocked_apply_is_not_a_plugin_failure_and_keeps_receipt()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = await AcceptBakeSuggestionHandler.HandleAsync(
                db,
                "s1",
                "make_level",
                "mcp_only",
                pluginApply: request => Task.FromResult(new JObject
                {
                    ["success"] = false,
                    ["error"] = "REVIT_BUSY: Revit is waiting for native user input. Use the local failure or operation queries.",
                    ["_operation"] = new JObject
                    {
                        ["operationId"] = "op-blocked-1",
                        ["execution"] = "completed",
                        ["outcome"] = "not_started",
                        ["complete"] = true,
                        ["mutationApplied"] = false
                    }
                }));

            var root = JObject.Parse(json);
            Assert.False((bool)root["ok"]!);
            Assert.Equal("plugin_apply_blocked", (string)root["error_code"]);
            Assert.Contains("REVIT_BUSY", (string)root["message"]);
            Assert.Equal("op-blocked-1", (string)root["_operation"]!["operationId"]);

            var stored = db.GetSuggestion("s1")!;
            Assert.Equal("open", stored.State);
            var attempts = (JArray)JObject.Parse(stored.PayloadJson)["accept_attempts"]!;
            Assert.Equal("plugin_apply_blocked", (string)attempts[0]!["error_code"]);
            Assert.False(db.ReadRegistryRecords().Any());
        }

        [Fact]
        public async Task AcceptBakeSuggestion_failed_apply_lifts_failure_receipt_to_top_level()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = await AcceptBakeSuggestionHandler.HandleAsync(
                db,
                "s1",
                "make_level",
                "mcp_only",
                pluginApply: request => Task.FromResult(new JObject
                {
                    ["success"] = false,
                    ["error"] = "REVIT_FAILURE_ROLLED_BACK",
                    ["_failure"] = new JObject
                    {
                        ["operationId"] = "op-failed-1",
                        ["outcome"] = "rolled_back",
                        ["incidentIds"] = new JArray("inc-1")
                    },
                    ["_operation"] = new JObject
                    {
                        ["operationId"] = "op-failed-1",
                        ["execution"] = "completed",
                        ["outcome"] = "rolled_back",
                        ["complete"] = true
                    }
                }));

            var root = JObject.Parse(json);
            Assert.False((bool)root["ok"]!);
            Assert.Equal("op-failed-1", (string)root["_operation"]!["operationId"]);
            Assert.Equal("op-failed-1", (string)root["_failure"]!["operationId"]);
            Assert.Equal("rolled_back", (string)root["_operation"]!["outcome"]);

            var stored = db.GetSuggestion("s1")!;
            Assert.Equal("open", stored.State);
            Assert.False(db.ReadRegistryRecords().Any());
        }

        [Fact]
        public async Task AcceptBakeSuggestion_rejects_duplicate_registry_name_before_plugin_apply()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));
            Assert.True(db.TryInsertRegistryRecord(new BakedToolRecord
            {
                Name = "make_level",
                Description = "Existing accepted tool",
                Source = "accepted",
                ParamsSchema = "{}",
                CompatMap = "{}",
                ReviewedByUser = true,
                CreatedAt = DateTimeOffset.Parse("2026-04-01T00:00:00Z").ToString("o"),
                FailureRate = 0,
                VersionHistoryBlob = "[]"
            }));

            var pluginApplyCalled = false;
            var json = await AcceptBakeSuggestionHandler.HandleAsync(
                db,
                "s1",
                "make_level",
                "mcp_only",
                pluginApply: request =>
                {
                    pluginApplyCalled = true;
                    return Task.FromResult(new JObject { ["success"] = true });
                });

            var root = JObject.Parse(json);
            Assert.False((bool)root["ok"]!);
            Assert.Equal("duplicate_tool_name", (string)root["error_code"]);
            Assert.False(pluginApplyCalled);
            Assert.Single(db.ReadRegistryRecords());
            Assert.Equal("open", db.GetSuggestion("s1")!.State);
        }

        [Fact]
        public async Task AcceptBakeSuggestion_plugin_success_marks_accepted_and_inserts_registry_after_apply()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));

            var json = await AcceptBakeSuggestionHandler.HandleAsync(
                db,
                "s1",
                "make_level",
                "ribbon_plus_mcp",
                pluginApply: request => Task.FromResult(new JObject
                {
                    ["success"] = true,
                    ["tool_name"] = (string)request["tool_name"],
                    ["display_name"] = (string)request["display_name"],
                    ["description"] = (string)request["description"],
                    ["source"] = (string)request["source"],
                    ["source_code"] = "compiled source",
                    ["params_schema"] = (string)request["params_schema"],
                    ["dll_base64"] = System.Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                    ["revit_version"] = "2026"
                }));

            var root = JObject.Parse(json);
            Assert.True((bool)root["ok"]!);
            Assert.Equal("accepted", db.GetSuggestion("s1")!.State);

            var record = Assert.Single(db.ReadRegistryRecords());
            Assert.Equal("make_level", record.Name);
            Assert.Equal("Suggested repeated workflow.", record.Description);
            Assert.Equal("preset", record.Source);
            Assert.Equal("s1", record.CreatedFromSuggestionId);
            Assert.Equal(new byte[] { 1, 2, 3 }, record.DllBytes);
            Assert.Equal("compiled source", record.SourceCode);
            Assert.Contains("ribbon_plus_mcp", record.CompatMap);
        }

        [Theory]
        [InlineData("preset")]
        [InlineData("macro")]
        public async Task AcceptBakeSuggestion_generated_wrapper_does_not_always_fail_smoke(string source)
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", source + ":workflow", source));

            string capturedSource = null;
            var json = await AcceptBakeSuggestionHandler.HandleAsync(
                db,
                "s1",
                "safe_workflow",
                "mcp_only",
                pluginApply: request =>
                {
                    capturedSource = (string)request["source_code"];
                    return Task.FromResult(new JObject
                    {
                        ["success"] = true,
                        ["tool_name"] = (string)request["tool_name"],
                        ["display_name"] = (string)request["display_name"],
                        ["description"] = (string)request["description"],
                        ["source"] = (string)request["source"],
                        ["source_code"] = capturedSource,
                        ["params_schema"] = (string)request["params_schema"],
                        ["dll_base64"] = System.Convert.ToBase64String(new byte[] { 4, 5, 6 }),
                        ["revit_version"] = "2026"
                    });
                });

            var root = JObject.Parse(json);
            Assert.True((bool)root["ok"]!);
            Assert.True(BakedToolRuntimeSource.HasMarker(capturedSource));
            Assert.True(BakedToolRuntimeSource.TryParse(capturedSource, out var spec));
            Assert.Equal(source, spec.Kind);
            Assert.DoesNotContain("CommandResult.Fail", capturedSource);
            Assert.DoesNotContain("CommandResult.Ok", capturedSource);
        }

        [Fact]
        public void AcceptBakeSuggestion_send_code_requires_anthropic_key_and_caches_no_failure_state()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "send_code:abc", "send_code"));

            var json = AcceptBakeSuggestionHandler.Handle(
                db,
                "s1",
                "condensed_tool",
                "mcp_only",
                envLookup: _ => null);

            var root = JObject.Parse(json);
            Assert.False((bool)root["ok"]!);
            Assert.Equal("missing_anthropic_api_key", (string)root["error_code"]);
            Assert.Equal(
                "Adaptive bake accept for send_code clusters requires ANTHROPIC_API_KEY. Set the env var and restart the MCP server. Cluster B/C accepts work without an API key.",
                (string)root["message"]);
            Assert.Equal("open", db.GetSuggestion("s1")!.State);
        }

        [Fact]
        public void AcceptBakeSuggestion_send_code_caches_condensed_output_and_reuses_it()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "send_code:abc", "send_code"));
            var condenser = new FakeCondenser();

            var first = AcceptBakeSuggestionHandler.Handle(
                db,
                "s1",
                "condensed_tool",
                "mcp_only",
                envLookup: key => key == "ANTHROPIC_API_KEY" ? "present" : null,
                codeCondenser: condenser);

            Assert.Equal("plugin_apply_unavailable", (string)JObject.Parse(first)["error_code"]);
            Assert.Equal(1, condenser.Calls);
            var payload = JObject.Parse(db.GetSuggestion("s1")!.PayloadJson);
            Assert.Equal("return CommandResult.Ok(new { ok = true });", (string)payload["condensed_code"]);
            Assert.Equal(@"{""type"":""object"",""properties"":{}}", (string)payload["condensed_params_schema"]);

            AcceptBakeSuggestionHandler.Handle(
                db,
                "s1",
                "condensed_tool",
                "mcp_only",
                envLookup: _ => null,
                codeCondenser: null);

            Assert.Equal(1, condenser.Calls);
        }

        [Fact]
        public void AcceptBakeSuggestion_send_code_uses_real_cluster_material_samples()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            using var db = NewDb(sandbox);
            var logger = new UsageEventLogger(
                paths,
                new RvtMcpConfig
                {
                    EnableAdaptiveBake = true,
                    CacheSendCodeBodies = true
                },
                analysisThrottle: TimeSpan.Zero);

            for (var i = 0; i < 9; i++)
            {
                logger.RecordToolCall(
                    "send_code_to_revit",
                    "{\"code\":\"return \\\"C:\\\\Users\\\\Admin\\\\Project" + i + ".rvt\\\";\"}",
                    success: true);
            }

            var suggestion = Assert.Single(new SuggestionProposer().Propose(
                logger.RefreshCandidates(),
                Array.Empty<BakeSuggestionRecord>()));
            db.UpsertSuggestion(suggestion);
            var condenser = new FakeCondenser();

            AcceptBakeSuggestionHandler.Handle(
                db,
                suggestion.Id,
                "condensed_tool",
                "mcp_only",
                envLookup: key => key == "ANTHROPIC_API_KEY" ? "present" : null,
                codeCondenser: condenser);

            Assert.Equal(1, condenser.Calls);
            var sample = Assert.Single(condenser.LastSamples);
            Assert.Contains("<project_file>", sample);
            Assert.DoesNotContain("Project0", sample);
        }

        [Fact]
        public void ListBakeSuggestions_refreshes_candidates_from_persisted_usage()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            using var db = NewDb(sandbox);
            var writer = new UsageEventLogger(
                paths,
                new RvtMcpConfig { EnableAdaptiveBake = true },
                analysisThrottle: TimeSpan.Zero);
            for (var i = 0; i < 15; i++)
            {
                writer.RecordToolCall("create_level", @"{""elevation"":3000,""name"":""Level 02""}", success: true);
            }

            var restartedLogger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = true });

            var json = ListBakeSuggestionsHandler.Handle(
                db,
                restartedLogger,
                DateTimeOffset.UtcNow);

            var suggestion = Assert.Single((JArray)JObject.Parse(json)["suggestions"]!);
            Assert.Equal("preset", (string)suggestion["source"]);
            Assert.Equal("open", (string)suggestion["state"]);
        }

        [Fact]
        public void AcceptBakeSuggestion_records_failed_attempt_in_existing_tool_history()
        {
            using var sandbox = new TempDir();
            using var db = NewDb(sandbox);
            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "preset"));
            Assert.True(db.TryInsertRegistryRecord(new BakedToolRecord
            {
                Name = "make_level",
                Description = "Existing accepted tool",
                Source = "accepted",
                ParamsSchema = "{}",
                CompatMap = "{}",
                ReviewedByUser = true,
                CreatedAt = DateTimeOffset.Parse("2026-04-01T00:00:00Z").ToString("o"),
                FailureRate = 0,
                VersionHistoryBlob = "[]"
            }));

            AcceptBakeSuggestionHandler.Handle(db, "s1", "make_level", "mcp_only");

            var registry = db.ReadRegistryRecords().Single(r => r.Name == "make_level");
            var history = JArray.Parse(registry.VersionHistoryBlob);
            Assert.Equal("accept_attempt_failed", (string)history[0]!["event"]);
            Assert.Equal("duplicate_tool_name", (string)history[0]!["suggestion_error_code"]);
        }

        private static BakeDb NewDb(TempDir sandbox)
        {
            var db = new BakeDb(Path.Combine(sandbox.Path, "bake.db"));
            db.Migrate();
            return db;
        }

        private static BakeSuggestionRecord Suggestion(string id, string key, string source)
        {
            return new BakeSuggestionRecord
            {
                Id = id,
                ClusterKey = key,
                Source = source,
                Title = source == "send_code" ? "Condense Revit Code" : "Create Level",
                Description = "Suggested repeated workflow.",
                State = "open",
                Score = 0.91,
                CreatedAt = "2026-04-27T00:00:00.0000000+00:00",
                UpdatedAt = "2026-04-27T00:00:00.0000000+00:00",
                PayloadJson = source == "send_code"
                    ? @"{""tool"":""send_code_to_revit"",""normalized_key"":""send_code:abc"",""output_choices"":[""mcp_only"",""ribbon_plus_mcp""],""code_cache_samples"":[""return 1;"",""return 2;"",""return 3;""]}"
                    : source == "macro"
                    ? @"{""tool"":""macro"",""normalized_key"":""macro:create_level,create_grid"",""output_choices"":[""mcp_only"",""ribbon_plus_mcp""],""sequence"":[""create_level"",""create_grid""],""sample"":{""parameter_kinds"":{}}}"
                    : @"{""tool"":""create_level"",""normalized_key"":""preset:create_level:elevation,name"",""output_choices"":[""mcp_only"",""ribbon_plus_mcp""],""sample"":{""parameter_kinds"":{""elevation"":""number"",""name"":""string""}}}",
                VersionHistoryBlob = "[]"
            };
        }

        private static string GetQueryValue(string url, string key)
        {
            var queryStart = url.IndexOf('?', StringComparison.Ordinal);
            Assert.True(queryStart >= 0, "URL did not include a query string.");

            foreach (var pair in url.Substring(queryStart + 1).Split('&'))
            {
                var equals = pair.IndexOf('=');
                var name = equals < 0 ? pair : pair.Substring(0, equals);
                if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal))
                    continue;

                var value = equals < 0 ? string.Empty : pair.Substring(equals + 1);
                return Uri.UnescapeDataString(value.Replace("+", "%20"));
            }

            throw new Xunit.Sdk.XunitException("Missing query value: " + key);
        }

        private static JObject ParseObjectNoDates(string json)
        {
            using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json))
            {
                DateParseHandling = Newtonsoft.Json.DateParseHandling.None
            };
            return JObject.Load(reader);
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bimwright-suggestions-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
        }

        private sealed class FakeCondenser : ICodeCondenser
        {
            public int Calls { get; private set; }
            public System.Collections.Generic.IReadOnlyList<string> LastSamples { get; private set; } = Array.Empty<string>();

            public CondensedBakeCode Condense(System.Collections.Generic.IReadOnlyList<string> redactedSamples)
            {
                Calls++;
                LastSamples = redactedSamples.ToArray();
                return new CondensedBakeCode
                {
                    Code = "return CommandResult.Ok(new { ok = true });",
                    ParamsSchema = @"{""type"":""object"",""properties"":{}}"
                };
            }
        }
    }
}
