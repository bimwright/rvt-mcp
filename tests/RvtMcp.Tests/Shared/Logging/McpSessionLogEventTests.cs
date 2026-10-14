using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    // Shares the mutable static McpSessionLog.ConfigLoader with McpSessionLogPrivacyTests;
    // same collection => xUnit runs them sequentially, preventing a cross-class race.
    [Collection("McpSessionLogConfig")]
    public class McpSessionLogEventTests
    {
        [Fact]
        public void EntryAdded_FiresOncePerAdd_AfterRedaction()
        {
            var log = new McpSessionLog();
            McpSessionLog.ConfigLoader = () => new RvtMcpConfig { CacheSendCodeBodies = false };
            McpCallEntry captured = null;
            var count = 0;

            log.EntryAdded += entry =>
            {
                count++;
                captured = entry;
            };

            try
            {
                log.Add(new McpCallEntry
                {
                    ToolName = "send_code_to_revit",
                    ParamsJson = @"{""code"":""return 1;""}",
                    CodeSnippet = "return 1;",
                    Success = true,
                    DurationMs = 5,
                    Summary = "Executed sensitive"
                });

                Assert.Equal(1, count);
                Assert.NotNull(captured);
                Assert.DoesNotContain("return 1", captured.ParamsJson);
                Assert.DoesNotContain("sensitive", captured.Summary);
            }
            finally
            {
                McpSessionLog.ConfigLoader = () => RvtMcpConfig.Load();
            }
        }
    }
}
