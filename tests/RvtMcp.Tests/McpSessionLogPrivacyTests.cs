using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    // Shares the mutable static McpSessionLog.ConfigLoader with McpSessionLogEventTests;
    // same collection => xUnit runs them sequentially, preventing a cross-class race.
    [Collection("McpSessionLogConfig")]
    public class McpSessionLogPrivacyTests
    {
        [Fact]
        public void Add_RedactsSendCodeBodyWhenBodyCacheDisabled()
        {
            var log = new McpSessionLog();
            McpSessionLog.ConfigLoader = () => new RvtMcpConfig { CacheSendCodeBodies = false };
            try
            {
                log.Add(new McpCallEntry
                {
                    ToolName = "send_code_to_revit",
                    ParamsJson = @"{""code"":""return \""SensitiveWallType42\"";"",""transactionMode"":""none""}",
                    CodeSnippet = "return \"SensitiveWallType42\";",
                    Success = true,
                    DurationMs = 10,
                    Summary = "Executed SensitiveWallType42"
                });

                var entry = log.Entries[0];

                Assert.DoesNotContain("SensitiveWallType42", entry.ParamsJson);
                Assert.DoesNotContain("transactionMode", entry.ParamsJson);
                Assert.Null(entry.CodeSnippet);
                Assert.DoesNotContain("SensitiveWallType42", entry.Summary);
                Assert.Contains("code_hash", entry.ParamsJson);
                Assert.Contains("code_length", entry.ParamsJson);
            }
            finally
            {
                McpSessionLog.ConfigLoader = () => RvtMcpConfig.Load();
            }
        }

        [Fact]
        public void Add_KeepsSendCodeBodyWhenBodyCacheEnabled()
        {
            var log = new McpSessionLog();
            McpSessionLog.ConfigLoader = () => new RvtMcpConfig { CacheSendCodeBodies = true };
            try
            {
                log.Add(new McpCallEntry
                {
                    ToolName = "send_code_to_revit",
                    ParamsJson = @"{""code"":""return \""SensitiveWallType42\"";""}",
                    CodeSnippet = "return \"SensitiveWallType42\";",
                    Success = true,
                    DurationMs = 10
                });

                var entry = log.Entries[0];

                Assert.Contains("SensitiveWallType42", entry.ParamsJson);
                Assert.Contains("SensitiveWallType42", entry.CodeSnippet);
            }
            finally
            {
                McpSessionLog.ConfigLoader = () => RvtMcpConfig.Load();
            }
        }

        [Fact]
        public void Add_TruncatesOversizedParamsAndFlagsEntry()
        {
            var log = new McpSessionLog();

            log.Add(new McpCallEntry
            {
                ToolName = "batch_execute",
                ParamsJson = new string('x', 70 * 1024),
                Success = true,
                DurationMs = 5
            });

            var entry = log.Entries[0];

            Assert.True(entry.ParamsTruncated);
            Assert.Contains("truncated", entry.ParamsJson);
            Assert.True(entry.ParamsJson.Length < 70 * 1024);
        }

        [Fact]
        public void Add_TruncatesCodeSnippetDisplayCopy_WhenBodyCacheEnabled()
        {
            var log = new McpSessionLog();
            McpSessionLog.ConfigLoader = () => new RvtMcpConfig { CacheSendCodeBodies = true };
            try
            {
                var code = new string('x', 140 * 1024);
                var paramsJson = "{\"code\":\"" + code + "\"}";
                log.Add(new McpCallEntry
                {
                    ToolName = "send_code_to_revit",
                    ParamsJson = paramsJson,
                    CodeSnippet = code,
                    Success = true,
                    DurationMs = 5
                });

                var entry = log.Entries[0];

                // Display copy bounded; ParamsJson keeps the full body for re-run.
                Assert.EndsWith("... (truncated)", entry.CodeSnippet);
                Assert.True(entry.CodeSnippet.Length <= 128 * 1024 + 32);
                Assert.Equal(paramsJson, entry.ParamsJson);
                Assert.False(entry.ParamsTruncated);
            }
            finally
            {
                McpSessionLog.ConfigLoader = () => RvtMcpConfig.Load();
            }
        }

        [Fact]
        public void Add_KeepsLargeSendCodeParamsWhenBodyCacheEnabled()
        {
            var log = new McpSessionLog();
            McpSessionLog.ConfigLoader = () => new RvtMcpConfig { CacheSendCodeBodies = true };
            try
            {
                var big = "{\"code\":\"" + new string('x', 70 * 1024) + "\"}";
                log.Add(new McpCallEntry
                {
                    ToolName = "send_code_to_revit",
                    ParamsJson = big,
                    CodeSnippet = new string('x', 70 * 1024),
                    Success = true,
                    DurationMs = 5
                });

                var entry = log.Entries[0];

                Assert.False(entry.ParamsTruncated);
                Assert.Equal(big, entry.ParamsJson);
            }
            finally
            {
                McpSessionLog.ConfigLoader = () => RvtMcpConfig.Load();
            }
        }
    }
}
