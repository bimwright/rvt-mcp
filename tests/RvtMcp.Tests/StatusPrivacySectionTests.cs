using System;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class StatusPrivacySectionTests
    {
        [Fact]
        public void Build_Defaults_ShowsOffFlagsAndToastOnWhenPassed()
        {
            var text = StatusPrivacySection.Build(new RvtMcpConfig(), toastEnabled: true);

            Assert.Contains("Toast notifications: ON", text);
            Assert.Contains("ToolBaker tools: ON", text);
            Assert.Contains("send_code: ON", text);
            Assert.Contains("Call log: OFF", text);
            Assert.Contains("Adaptive bake suggestions: OFF", text);
            Assert.Contains("Cache send_code bodies (for bake clusters): OFF", text);
            Assert.Contains("Persist send_code journal (TTL): OFF", text);
            Assert.Contains("Default privacy", text);
        }

        [Fact]
        public void Build_WhenPersistActive_ShowsUntil()
        {
            var until = DateTimeOffset.UtcNow.AddHours(4).ToString("o");
            var config = new RvtMcpConfig
            {
                EnableCallLog = true,
                EnableAdaptiveBake = true,
                CacheSendCodeBodies = true,
                PersistSendCodeBodies = true,
                PersistSendCodeBodiesUntil = until
            };

            var text = StatusPrivacySection.Build(config, toastEnabled: false);

            Assert.Contains("Toast notifications: OFF", text);
            Assert.Contains("Adaptive bake suggestions: ON", text);
            Assert.Contains("Cache send_code bodies (for bake clusters): ON", text);
            Assert.Contains("Persist send_code journal (TTL): ON until", text);
            Assert.Contains(until, text);
        }

        [Fact]
        public void Build_NullConfig_DoesNotThrow()
        {
            var text = StatusPrivacySection.Build(null, toastEnabled: false);
            Assert.Contains("Adaptive bake suggestions: OFF", text);
        }
    }
}
