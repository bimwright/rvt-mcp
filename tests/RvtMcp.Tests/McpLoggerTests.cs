using RvtMcp.Plugin;
using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("Sequential")]
    public class McpLoggerTests
    {
        [Fact]
        public void BuildLogSafePayload_RedactsSendCodeParamsAndCode()
        {
            const string codeBody = "return \"SensitiveWallType42\";";
            var paramsJson = @"{""code"":""return \""SensitiveWallType42\"";"",""transactionMode"":""none"",""viewName"":""Level 01""}";

            var safe = McpLogger.BuildLogSafePayload("send_code_to_revit", paramsJson, codeBody);

            var metadata = JObject.FromObject(safe.Params);
            var serialized = JsonConvert.SerializeObject(new { safe.Code, safe.Params });

            Assert.Null(safe.Code);
            Assert.Equal(BakeRedactor.HashBody(codeBody), (string)metadata["code_hash"]);
            Assert.Equal(codeBody.Length, (int)metadata["code_length"]);
            Assert.DoesNotContain("SensitiveWallType42", serialized);
            Assert.DoesNotContain("transactionMode", serialized);
            Assert.DoesNotContain("Level 01", serialized);
        }

        [Fact]
        public void BuildLogSafeResult_RedactsSendCodeResult()
        {
            var result = McpLogger.BuildLogSafeResult(
                "send_code_to_revit",
                @"{""path"":""C:\\Users\\Admin\\Documents\\Project A&B.rvt"",""family"":""Door Type A+B.rfa"",""viewName"":""Level 01""}");

            Assert.DoesNotContain("Project A&B", result);
            Assert.DoesNotContain("Door Type A+B", result);
            Assert.DoesNotContain(@"C:\\Users\\Admin\\Documents", result);
            Assert.DoesNotContain("Level 01", result);
            Assert.Contains("<project_file>", result);
            Assert.Contains("<family_file>", result);
        }

        [Fact]
        public void BuildLogSafeResult_RedactsActualSendCodeResultShape()
        {
            var result = McpLogger.BuildLogSafeResult(
                "send_code_to_revit",
                @"{""executed"":true,""result"":""Level 01 - Coordination""}");

            Assert.DoesNotContain("Level 01 - Coordination", result);
            Assert.Contains("<result_1>", result);
        }

        [Fact]
        public void BuildLogSafeResult_PreservesBenignNonSendCodeResultShape()
        {
            var result = McpLogger.BuildLogSafeResult(
                "get_current_view_info",
                @"{""result"":""OK"",""count"":3}");

            Assert.Equal(@"{""result"":""OK"",""count"":3}", result);
        }

        [Fact]
        public void BuildLogSafePayload_RedactsNonSendCodeParamsAndCode()
        {
            var paramsJson = @"{""path"":""C:\\Users\\Admin\\Documents\\Project A&B.rvt"",""viewName"":""Level 01"",""apiKey"":""sk-testsecret12345""}";

            var safe = McpLogger.BuildLogSafePayload(
                "get_current_view_info",
                paramsJson,
                "return \"Project A&B\";");

            var serialized = JsonConvert.SerializeObject(new { safe.Code, safe.Params });

            Assert.DoesNotContain("Project A&B", serialized);
            Assert.DoesNotContain(@"C:\\Users\\Admin\\Documents", serialized);
            Assert.DoesNotContain("Level 01", serialized);
            Assert.DoesNotContain("sk-testsecret12345", serialized);
            Assert.Contains("<project_file>", serialized);
        }

        [Fact]
        public void BuildLogSafeResult_RedactsNonSendCodeResult()
        {
            const string resultJson = @"{""path"":""C:\\Users\\Admin\\Documents\\Project A&B.rvt"",""viewName"":""Level 01""}";

            var result = McpLogger.BuildLogSafeResult("get_current_view_info", resultJson);

            Assert.DoesNotContain("Project A&B", result);
            Assert.DoesNotContain(@"C:\\Users\\Admin\\Documents", result);
            Assert.DoesNotContain("Level 01", result);
            Assert.Contains("<project_file>", result);
        }

        [Fact]
        public void Log_RedactsPersistentErrorField()
        {
            var root = Path.Combine(Path.GetTempPath(), "bimwright-mcp-logger-" + Guid.NewGuid().ToString("N"));
            try
            {
                McpLogger.LocalAppDataOverride = root;
                McpLogger.Initialize();

                McpLogger.Log(
                    "send_code_to_revit",
                    @"{""code"":""return doc.PathName;""}",
                    success: false,
                    durationMs: 5,
                    errorMsg: @"Could not open C:\Users\Admin\Documents\Project A&B.rvt from https://example.com/client?token=sk-testsecret12345");

                var line = File.ReadAllText(Path.Combine(root, "Bimwright", "rvt-mcp", "mcp-calls.jsonl"));

                Assert.DoesNotContain("Project A&B", line);
                Assert.DoesNotContain(@"C:\Users\Admin\Documents", line);
                Assert.DoesNotContain("https://example.com", line);
                Assert.DoesNotContain("sk-testsecret12345", line);
                Assert.Contains("<local_path>", line);
            }
            finally
            {
                McpLogger.LocalAppDataOverride = null;
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Initialize_DeletesPreV5ActiveLogInsteadOfArchiving()
        {
            var root = Path.Combine(Path.GetTempPath(), "bimwright-mcp-logger-" + Guid.NewGuid().ToString("N"));
            try
            {
                var dir = Path.Combine(root, "Bimwright", "rvt-mcp");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "mcp-calls.version"), "3");
                File.WriteAllText(Path.Combine(dir, "mcp-calls.jsonl"), "raw send_code_to_revit body");

                McpLogger.LocalAppDataOverride = root;
                McpLogger.Initialize();

                Assert.False(File.Exists(Path.Combine(dir, "mcp-calls.jsonl")));
                Assert.Empty(Directory.GetFiles(dir, "mcp-calls-*.jsonl"));
                Assert.Equal("5", File.ReadAllText(Path.Combine(dir, "mcp-calls.version")));
            }
            finally
            {
                McpLogger.LocalAppDataOverride = null;
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Initialize_DeletesPreV5ArchivedLogs()
        {
            var root = Path.Combine(Path.GetTempPath(), "bimwright-mcp-logger-" + Guid.NewGuid().ToString("N"));
            try
            {
                var dir = Path.Combine(root, "Bimwright", "rvt-mcp");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "mcp-calls.version"), "3");
                File.WriteAllText(Path.Combine(dir, "mcp-calls-20260426-010101.jsonl"), "raw send_code_to_revit body");

                McpLogger.LocalAppDataOverride = root;
                McpLogger.Initialize();

                Assert.Empty(Directory.GetFiles(dir, "mcp-calls-*.jsonl"));
                Assert.Equal("5", File.ReadAllText(Path.Combine(dir, "mcp-calls.version")));
            }
            finally
            {
                McpLogger.LocalAppDataOverride = null;
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Initialize_DeletesVersion4OrphanArchivedLogs()
        {
            var root = Path.Combine(Path.GetTempPath(), "bimwright-mcp-logger-" + Guid.NewGuid().ToString("N"));
            try
            {
                var dir = Path.Combine(root, "Bimwright", "rvt-mcp");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "mcp-calls.version"), "4");
                File.WriteAllText(Path.Combine(dir, "mcp-calls-20260426-010101.jsonl"), "raw send_code_to_revit body");

                McpLogger.LocalAppDataOverride = root;
                McpLogger.Initialize();

                Assert.Empty(Directory.GetFiles(dir, "mcp-calls-*.jsonl"));
                Assert.Equal("5", File.ReadAllText(Path.Combine(dir, "mcp-calls.version")));
            }
            finally
            {
                McpLogger.LocalAppDataOverride = null;
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }
}
