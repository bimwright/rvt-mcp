using System;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("L10n")]
    public sealed class ToastActivityTooltipTests : IDisposable
    {
        public ToastActivityTooltipTests()
        {
            L.ResetForTests();
            var en = EmbeddedCatalog.Load(typeof(ToastActivityTooltipTests).Assembly, "en");
            L.InitializeForTests(StringTable.Build("en", en, null, null));
        }

        public void Dispose() => L.ResetForTests();

        [Fact]
        public void Tooltip_has_title_body_status_local_time_and_duration()
        {
            var when = new DateTimeOffset(2026, 10, 2, 8, 30, 15, TimeSpan.Zero);
            var entry = new ToastActivityEntry("Create sheet", "Sheet A101 created", true, 830, when);
            var lines = entry.TooltipText.Split('\n');
            Assert.Equal("Create sheet", lines[0]);
            Assert.Equal("Sheet A101 created", lines[1]);
            Assert.StartsWith("Success · " + entry.LocalTimeText, lines[2]);
            Assert.EndsWith(" · 830 ms", lines[2]);
        }

        [Fact]
        public void Tooltip_marks_failures_and_omits_unmeasured_duration_and_empty_body()
        {
            var entry = new ToastActivityEntry("Delete element", "", false);
            var lines = entry.TooltipText.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Equal("Delete element", lines[0]);
            Assert.StartsWith("Failed · ", lines[1]);
            Assert.DoesNotContain(" ms", lines[1]);
        }

        [Fact]
        public void Tooltip_uses_only_the_redacted_bounded_copy()
        {
            var entry = new ToastActivityEntry("Tool",
                "password=super-private; C:\\private\\model.rvt " + new string('x', 400), true);
            Assert.DoesNotContain("super-private", entry.TooltipText);
            Assert.DoesNotContain("private", entry.TooltipText.Replace("<redacted>", string.Empty));
            Assert.Contains("<redacted>", entry.TooltipText);
            Assert.True(entry.TooltipText.Length < ToastActivityEntry.TitleLimit + ToastActivityEntry.BodyLimit + 80);
        }
    }
}
