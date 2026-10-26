using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using RvtMcp.Server;
using RvtMcp.Server.Bake;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("Sequential")]
    public class ScriptLoggingPipelineTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Native_logs_add_receipts_without_raw_data_or_changing_existing_archives(bool persistBodies)
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-script-log-" + Guid.NewGuid().ToString("N"));
            var oldRoot = McpLogger.LocalAppDataOverride; var oldJournal = SendCodeJournal.LocalAppDataOverride;
            try
            {
                var directory = Path.Combine(root, "Bimwright", "rvt-mcp");
                ScriptBaselineFixture.CopyHistory(directory);
                var before = Directory.GetFiles(directory).ToDictionary(p => p, File.ReadAllBytes);
                McpLogger.LocalAppDataOverride = root; SendCodeJournal.LocalAppDataOverride = directory;
                McpLogger.Initialize(enabled: true);
                var input = new ScriptInput("return data;\r\n", ScriptData.FromJson("{\"label\":\"PRIVATE-RUNTIME-DATA\",\"n\":1.0000000000000000001}"), resultContract: ScriptModes.TaskV1);
                var request = new ScriptWireRequest(input);
                var state = new ScriptExecutionState(JObject.FromObject(request.Metadata()), request.Parameters().ToString());
                state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Fail("VERIFY_FAILED", "Verification failed"));
                var envelope = ScriptOutcomePipelineTests.Envelope("partial"); state.Apply(envelope);
                Assert.Equal(typeof(void), typeof(McpLogger).GetMethod("Log")!.ReturnType);
                Assert.Equal(9, typeof(McpLogger).GetMethod("Log")!.GetParameters().Length);
                Assert.Equal(8, typeof(SendCodeJournal).GetMethod("TryAppend")!.GetParameters().Length);
                Assert.True(McpLogger.TryLog("send_code_to_revit", request.Parameters().ToString(), false, 10, "Verification failed",
                    resultJson: new JObject { ["result"] = "PRIVATE-RUNTIME-DATA" }.ToString(), receipt: envelope));
                foreach (var file in before)
                {
                    var bytes = File.ReadAllBytes(file.Key);
                    if (Path.GetFileName(file.Key) == "mcp-calls.jsonl") Assert.True(bytes.Take(file.Value.Length).SequenceEqual(file.Value));
                    else Assert.Equal(file.Value, bytes);
                }
                Assert.Equal("5", File.ReadAllText(Path.Combine(directory, "mcp-calls.version")).Trim());
                var line = JObject.Parse(File.ReadAllLines(McpLogger.CurrentLogPath).Last());
                Assert.False(line.Value<bool>("success")); Assert.Equal("failed", line["task_status"]);
                Assert.Equal("VERIFY_FAILED", line["task_error_code"]);
                Assert.Equal(ScriptIdentity.CodeSha256(input.Code), line["script_code_sha256"]);
                Assert.Equal(input.Data.Hash, line["data_hash"]);
                Assert.Equal("partial", line["_operation"]!["outcome"]);
                Assert.DoesNotContain("PRIVATE-RUNTIME-DATA", line.ToString());
                var history = SessionLogHistoryLoader.LoadPastSessions(directory, "unrelated-session", 0);
                Assert.Contains(history, e => e.Script == null && e.IsHistorical);
                var latest = Assert.Single(history.Where(e => e.Script != null));
                Assert.False(latest.Success); Assert.Equal("failed", latest.Task!["status"]);
                Assert.Contains("native: partial", latest.Summary);
                Assert.Equal("partial", latest.Operation!["outcome"]);
                var config = new RvtMcpConfig {EnableCallLog = true, PersistSendCodeBodies = persistBodies,
                    PersistSendCodeBodiesUntil = DateTimeOffset.UtcNow.AddDays(1).ToString("o")};
                var saved = SendCodeJournal.TryAppendWithReceipt(config, "test", input.Code, false, 10, "Verification failed", null, receipt: envelope);
                Assert.Equal(persistBodies, saved);
                if (persistBodies)
                {
                    var journal = File.ReadAllText(SendCodeJournal.JournalPath);
                    Assert.DoesNotContain("PRIVATE-RUNTIME-DATA", journal);
                    Assert.Equal("VERIFY_FAILED", JObject.Parse(journal)["task_error_code"]);
                }
            }
            finally
            {
                McpLogger.LocalAppDataOverride = oldRoot; SendCodeJournal.LocalAppDataOverride = oldJournal;
                McpLogger.Initialize(enabled: false);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Usage_audit_waits_for_final_receipt_and_never_clusters_runtime_input(bool cacheBodies)
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-script-usage-" + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new BakePaths(root);
                var logger = new UsageEventLogger(paths, new RvtMcpConfig {EnableAdaptiveBake = true, CacheSendCodeBodies = cacheBodies});
                var input = new ScriptInput("return data;", ScriptData.FromJson("{\"value\":\"PRIVATE-RUNTIME-DATA\"}"), resultContract: ScriptModes.TaskV1);
                var request = new ScriptWireRequest(input); var parameters = request.Parameters().ToString();
                Assert.Null(logger.RecordToolCall("send_code_to_revit", parameters, false));
                Assert.False(File.Exists(paths.UsageJsonl));
                var state = new ScriptExecutionState(JObject.FromObject(request.Metadata()), parameters);
                var pending = new GatewayPendingRequest("operation", 1, script: state.Script, finalizeScript: final => Assert.True(logger.RecordScriptCall(final)));
                pending.Timeout(); Assert.False(File.Exists(paths.UsageJsonl));
                state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Fail("VERIFY_FAILED", "Check"));
                var terminal = ScriptOutcomePipelineTests.Envelope(); state.Apply(terminal); pending.Route(terminal); pending.Route(terminal);
                var line = Assert.Single(File.ReadAllLines(paths.UsageJsonl));
                Assert.DoesNotContain("PRIVATE-RUNTIME-DATA", line); Assert.DoesNotContain("return data", line);
                var entry = JsonConvert.DeserializeObject<UsageEvent>(line)!;
                Assert.Equal("script", entry.Source); Assert.False(entry.Success);
                var payload = JObject.Parse(entry.PayloadJson);
                Assert.Equal(input.Data.Hash, payload["data_hash"]); Assert.Equal("failed", payload["task_status"]);
                Assert.Equal("ineligible", payload["observation_status"]); Assert.Empty(logger.RefreshCandidates());
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void Server_journal_retains_metadata_even_when_result_text_is_truncated()
        {
            var state = ScriptOutcomePipelineTests.State(); state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Fail("VERIFY_FAILED", "Check"));
            var envelope = ScriptOutcomePipelineTests.Envelope(); state.Apply(envelope); envelope["data"] = new string('x', 50000);
            var entry = JournalEntry.Create("send_code_to_revit", "{\"code\":\"return null;\",\"data_json\":\"{\\\"value\\\":\\\"PRIVATE-RUNTIME-DATA\\\"}\"}", false, 10, resultJson: envelope.ToString());
            Assert.Equal("failed", entry.Task!["status"]); Assert.Equal("VERIFY_FAILED", entry.Task["error"]!["code"]);
            Assert.Equal("committed", entry.Operation!["outcome"]); Assert.NotNull(entry.Script!["logical_call_id"]);
            Assert.DoesNotContain("PRIVATE-RUNTIME-DATA", JsonConvert.SerializeObject(entry));
        }
    }

    [Collection("McpSessionLogConfig")]
    public class ScriptCachePrivacyTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Enhanced_session_entries_never_cache_runtime_data_for_replay(bool cacheBodies)
        {
            var oldLoader = McpSessionLog.ConfigLoader;
            try
            {
                McpSessionLog.ConfigLoader = () => new RvtMcpConfig {CacheSendCodeBodies = cacheBodies};
                var module = new ScriptModuleRevision("helper", "class PrivateHelper {}");
                var input = new ScriptInput("return data;", ScriptData.FromJson("{\"value\":\"PRIVATE-RUNTIME-DATA\"}"), new[] {"helper"}, resultContract: ScriptModes.TaskV1);
                var request = new ScriptWireRequest(input, new[] {module});
                var state = new ScriptExecutionState(JObject.FromObject(request.Metadata()), request.Parameters().ToString());
                state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Fail("VERIFY_FAILED", "Check"));
                var envelope = ScriptOutcomePipelineTests.Envelope("partial"); state.Apply(envelope);
                var log = new McpSessionLog();
                log.Add(new McpCallEntry { ToolName = "send_code_to_revit", ParamsJson = request.Parameters().ToString(), Success = false,
                    ErrorMessage = "VERIFY_FAILED", Script = envelope["_script"] as JObject, Task = envelope["_task"] as JObject, Operation = envelope["_operation"] as JObject });
                var entry = Assert.Single(log.Entries);
                Assert.DoesNotContain("PRIVATE-RUNTIME-DATA", entry.ParamsJson); Assert.DoesNotContain("PrivateHelper", entry.ParamsJson);
                if (cacheBodies) Assert.Equal(input.Code, entry.CodeSnippet); else Assert.Null(entry.CodeSnippet);
                Assert.Equal(input.Data.Hash, JObject.Parse(entry.ParamsJson)["data_hash"]);
                Assert.Contains("Script execute", entry.Summary); Assert.Contains("native: partial", entry.Summary); Assert.Contains("VERIFY_FAILED", entry.Summary);
                var toast = RvtMcp.Plugin.Views.Toast.ToastContentBuilder.BuildCompleted("send_code_to_revit", request.Parameters().ToString(), envelope.ToString(), false, "VERIFY_FAILED", 10, "Execute script");
                Assert.False(toast.Success); Assert.Contains("native: partial", toast.Title); Assert.Contains("VERIFY_FAILED", toast.Body);
                var check = ScriptOutcomePipelineTests.State(compileOnly: true);
                Assert.Equal("Compile check", ScriptPresentation.Label(check.Script));
                var invalid = new McpCallEntry {ToolName = "send_code_to_revit", ParamsJson = "{\"code\":\"return 7;\",\"modules\":{\"value\":\"PRIVATE-RUNTIME-DATA\"},\"usage_context\":{\"value\":\"PRIVATE-RUNTIME-DATA\"}}"};
                log.Add(invalid);
                Assert.DoesNotContain("PRIVATE-RUNTIME-DATA", invalid.ParamsJson);
            }
            finally { McpSessionLog.ConfigLoader = oldLoader; }
        }
    }
}
