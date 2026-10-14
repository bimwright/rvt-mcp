using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server.Bake;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class UsageEventLoggerTests
    {
        [Fact]
        public void RecordToolCall_does_not_create_usage_log_when_adaptive_bake_disabled()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            var logger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = false });

            logger.RecordToolCall("get_current_view_info", null, success: true);

            Assert.False(File.Exists(paths.UsageJsonl));
        }

        [Fact]
        public void RecordToolCall_redacts_send_code_body_by_default()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            var logger = new UsageEventLogger(paths, new RvtMcpConfig
            {
                EnableAdaptiveBake = true,
                CacheSendCodeBodies = false
            });

            logger.RecordToolCall(
                "send_code_to_revit",
                @"{""code"":""return \""SensitiveWallType42\"";""}",
                success: true);

            var line = File.ReadAllLines(paths.UsageJsonl).Single();
            var json = JObject.Parse(line);
            var payload = JObject.Parse((string)json["PayloadJson"]);

            Assert.Equal("send_code", (string)json["Source"]);
            Assert.Equal(BakeRedactor.HashBody("return \"SensitiveWallType42\";"), (string)json["BodyHash"]);
            Assert.Equal("return \"SensitiveWallType42\";".Length, (int)payload["code_length"]);
            Assert.False((bool)payload["body_cache_enabled"]);
            Assert.DoesNotContain("SensitiveWallType42", line);
            Assert.Null(payload["cluster_material"]);
        }

        [Fact]
        public void RecordToolCall_stores_only_redacted_send_code_cluster_material_when_enabled()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            var logger = new UsageEventLogger(paths, new RvtMcpConfig
            {
                EnableAdaptiveBake = true,
                CacheSendCodeBodies = true
            });

            logger.RecordToolCall(
                "send_code_to_revit",
                @"{""code"":""return doc.PathName + \"" C:\\Users\\Admin\\Project Alpha.rvt\"";""}",
                success: true);

            var line = File.ReadAllLines(paths.UsageJsonl).Single();
            var payload = JObject.Parse((string)JObject.Parse(line)["PayloadJson"]);
            var material = (string)payload["cluster_material"];

            Assert.True((bool)payload["body_cache_enabled"]);
            Assert.Contains("<project_file>", material);
            Assert.DoesNotContain("Project Alpha", material);
            Assert.DoesNotContain(@"C:\\Users\\Admin", material);
        }

        [Fact]
        public void RecordToolCall_normalizes_preset_argument_shape_without_volatile_values()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            var logger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = true });

            logger.RecordToolCall(
                "place_view_on_sheet",
                @"{""viewId"":123456,""sheetId"":987654,""sheetName"":""A101 - Client Plan"",""exportPath"":""C:\\Users\\Admin\\Project Alpha.rvt"",""timestamp"":""2026-04-27T00:00:00Z""}",
                success: true);

            var line = File.ReadAllLines(paths.UsageJsonl).Single();
            var json = JObject.Parse(line);
            var payload = JObject.Parse((string)json["PayloadJson"]);
            var serialized = payload.ToString();

            Assert.Equal("preset", (string)json["Source"]);
            Assert.DoesNotContain("123456", serialized);
            Assert.DoesNotContain("987654", serialized);
            Assert.DoesNotContain("Project Alpha", serialized);
            Assert.DoesNotContain("2026-04-27", serialized);
            Assert.Equal("string", (string)payload["parameter_kinds"]["sheetName"]);
            Assert.Null(payload["parameter_kinds"]["viewId"]);
            Assert.Null(payload["parameter_kinds"]["exportPath"]);
            Assert.Null(payload["parameter_kinds"]["timestamp"]);
        }

        [Fact]
        public void RecordToolCall_preserves_semantic_fields_that_end_with_id_letters()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            var logger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = true });

            logger.RecordToolCall(
                "create_grid",
                @"{""grid"":""A"",""valid"":true,""solid"":""Concrete"",""fluid"":""Water"",""viewId"":123,""sheetIds"":[456],""element_id"":789}",
                success: true);

            var line = File.ReadAllLines(paths.UsageJsonl).Single(line => line.Contains(@"""Source"":""preset"""));
            var payload = JObject.Parse((string)JObject.Parse(line)["PayloadJson"]);
            var kinds = (JObject)payload["parameter_kinds"];

            Assert.Equal("string", (string)kinds["grid"]);
            Assert.Equal("bool", (string)kinds["valid"]);
            Assert.Equal("string", (string)kinds["solid"]);
            Assert.Equal("string", (string)kinds["fluid"]);
            Assert.Null(kinds["viewId"]);
            Assert.Null(kinds["sheetIds"]);
            Assert.Null(kinds["element_id"]);
        }

        [Fact]
        public void RecordToolCall_records_adjacent_preset_sequence_as_ordered_macro()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            var logger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = true });

            logger.RecordToolCall("create_level", @"{""elevation"":3000,""name"":""Level 02""}", success: true);
            logger.RecordToolCall("create_grid", @"{""startX"":0,""startY"":0,""endX"":5000,""endY"":0,""name"":""A""}", success: true);

            var macro = File.ReadAllLines(paths.UsageJsonl)
                .Select(JObject.Parse)
                .Single(line => (string)line["Source"] == "macro");

            Assert.Equal("create_level>create_grid", (string)macro["Tool"]);
            Assert.Equal("macro:create_level>create_grid", (string)macro["NormalizedKey"]);
            Assert.True((bool)macro["Success"]);
        }

        [Fact]
        public void RecordToolCall_refreshes_candidates_from_usage_log_after_capture()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            var logger = new UsageEventLogger(
                paths,
                new RvtMcpConfig { EnableAdaptiveBake = true },
                analysisThrottle: TimeSpan.Zero);

            for (var i = 0; i < 15; i++)
            {
                logger.RecordToolCall("create_level", @"{""elevation"":3000,""name"":""Level 02""}", success: true);
            }

            var candidate = Assert.Single(logger.LastCandidates.Where(c => c.Source == "preset"));
            Assert.Equal("preset:create_level:elevation,name", candidate.NormalizedKey);
            Assert.Equal(15, candidate.Count);
        }

        [Fact]
        public void RefreshCandidates_reads_usage_log_and_forms_candidates()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.UsageJsonl));
            File.WriteAllLines(paths.UsageJsonl, PresetThresholdEvents());
            var logger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = true });

            var candidates = logger.RefreshCandidates(DateTimeOffset.Parse("2026-04-27T00:00:00Z"));

            var candidate = Assert.Single(candidates);
            Assert.Equal("preset:create_level:elevation,name", candidate.NormalizedKey);
            Assert.Equal(15, candidate.Count);
        }

        [Fact]
        public void RefreshCandidates_does_not_analyze_when_adaptive_bake_disabled()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.UsageJsonl));
            File.WriteAllLines(paths.UsageJsonl, PresetThresholdEvents());
            var logger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = false });

            var candidates = logger.RefreshCandidates(DateTimeOffset.Parse("2026-04-27T00:00:00Z"));
            logger.RecordToolCall("create_level", @"{""elevation"":3000,""name"":""Level 02""}", success: true);

            Assert.Empty(candidates);
            Assert.Empty(logger.LastCandidates);
            Assert.Equal(15, File.ReadAllLines(paths.UsageJsonl).Length);
        }

        [Fact]
        public void RefreshCandidates_uses_recent_tail_events_only()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.UsageJsonl));
            File.WriteAllLines(paths.UsageJsonl,
                PresetThresholdEvents("old-tail-excluded", "create_grid", "preset:create_grid:name", DateTimeOffset.Parse("2026-04-10T00:00:00Z"), @"{""parameter_kinds"":{""name"":""string""}}")
                    .Concat(PresetThresholdEvents()));
            var logger = new UsageEventLogger(
                paths,
                new RvtMcpConfig { EnableAdaptiveBake = true },
                replayLineLimit: 15);

            var candidates = logger.RefreshCandidates(DateTimeOffset.Parse("2026-04-27T00:00:00Z"));

            var candidate = Assert.Single(candidates);
            Assert.Equal("preset:create_level:elevation,name", candidate.NormalizedKey);
        }

        [Fact]
        public void RefreshCandidates_ignores_events_older_than_replay_window()
        {
            using var sandbox = new TempDir();
            var paths = new BakePaths(sandbox.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.UsageJsonl));
            File.WriteAllLines(paths.UsageJsonl,
                PresetThresholdEvents("stale", "create_level", "preset:create_level:elevation,name", DateTimeOffset.Parse("2026-03-01T00:00:00Z"), @"{""parameter_kinds"":{""elevation"":""number"",""name"":""string""}}")
                    .Concat(PresetThresholdEvents()));
            var logger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = true });

            var candidates = logger.RefreshCandidates(DateTimeOffset.Parse("2026-04-27T00:00:00Z"));

            var candidate = Assert.Single(candidates);
            Assert.Equal(15, candidate.Count);
            Assert.Equal(DateTimeOffset.Parse("2026-04-10T00:00:00Z"), candidate.FirstSeenUtc);
        }

        [Fact]
        public void RecordToolCall_reports_capture_failure_without_throwing()
        {
            using var sandbox = new TempDir();
            var blockedLocalAppData = Path.Combine(sandbox.Path, "blocked");
            File.WriteAllText(blockedLocalAppData, "not a directory");
            var logger = new UsageEventLogger(
                new BakePaths(blockedLocalAppData),
                new RvtMcpConfig { EnableAdaptiveBake = true },
                analysisThrottle: TimeSpan.Zero);
            var originalError = Console.Error;
            using var error = new StringWriter();
            Console.SetError(error);

            try
            {
                logger.RecordToolCall("create_level", @"{""elevation"":3000,""name"":""Level 02""}", success: true);
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.NotNull(logger.LastError);
            Assert.Contains("Usage capture failed", error.ToString());
        }

        private static IEnumerable<string> PresetThresholdEvents()
        {
            return PresetThresholdEvents(
                "preset",
                "create_level",
                "preset:create_level:elevation,name",
                DateTimeOffset.Parse("2026-04-10T00:00:00Z"),
                @"{""parameter_kinds"":{""elevation"":""number"",""name"":""string""}}");
        }

        private static IEnumerable<string> PresetThresholdEvents(
            string idPrefix,
            string tool,
            string normalizedKey,
            DateTimeOffset start,
            string payloadJson)
        {
            for (var i = 0; i < 15; i++)
            {
                yield return JsonConvert.SerializeObject(new UsageEvent
                {
                    Id = idPrefix + "-" + i,
                    TsUtc = start.AddDays(i),
                    Source = "preset",
                    Tool = tool,
                    NormalizedKey = normalizedKey,
                    PayloadJson = payloadJson,
                    Success = true
                }, Formatting.None);
            }
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bimwright-usage-test-" + Guid.NewGuid().ToString("N"));
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
