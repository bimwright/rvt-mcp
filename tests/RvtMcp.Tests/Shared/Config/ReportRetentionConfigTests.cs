using Newtonsoft.Json;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class ReportRetentionConfigTests
    {
        [Theory]
        [InlineData("{}", 30)]
        [InlineData("{\"reportRetentionDays\":1}", 1)]
        [InlineData("{\"reportRetentionDays\":3650}", 3650)]
        [InlineData("{\"reportRetentionDays\":\" 42 \"}", 42)]
        [InlineData("{\"reportRetentionDays\":0}", 30)]
        [InlineData("{\"reportRetentionDays\":-1}", 30)]
        [InlineData("{\"reportRetentionDays\":3651}", 30)]
        [InlineData("{\"reportRetentionDays\":2.5}", 30)]
        [InlineData("{\"reportRetentionDays\":true}", 30)]
        [InlineData("{\"reportRetentionDays\":[]}", 30)]
        [InlineData("{\"reportRetentionDays\":\"bad\"}", 30)]
        [InlineData("{\"reportRetentionDays\":null}", 30)]
        public void Config_value_is_normalized(string json, int expected)
        {
            Assert.Equal(expected, JsonConvert.DeserializeObject<RvtMcpConfig>(json).ReportRetentionDaysOrDefault);
        }
    }
}
