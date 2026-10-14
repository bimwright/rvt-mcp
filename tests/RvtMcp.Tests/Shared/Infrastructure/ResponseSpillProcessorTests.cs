using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ResponseSpillProcessorTests : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "rvtmcp-spill-processor-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Explicit_file_mode_replaces_bulk_data_with_successful_bounded_envelope()
        {
            var data = new JObject
            {
                ["projectName"] = "Demo",
                ["rooms"] = new JArray(new JObject { ["elementId"] = 1, ["name"] = "Lobby" })
            };
            var rawResponse = JsonConvert.SerializeObject(new { success = true, data });
            var processor = new ResponseSpillProcessor(new ResponseSpillWriter(_directory));

            var outcome = processor.Process(
                "export_room_data",
                "{\"output\":\"file\"}",
                true,
                data,
                rawResponse);

            Assert.True(outcome.Spilled);
            Assert.False(outcome.Automatic);
            var envelope = Assert.IsType<JObject>(outcome.Data);
            Assert.True(envelope.Value<bool>("success"));
            Assert.Equal("file", envelope.Value<string>("output_mode"));
            Assert.True(File.Exists(envelope.Value<string>("path")));
            Assert.Equal("sqlite", envelope.Value<string>("format"));
            Assert.True(Encoding.UTF8.GetByteCount(envelope.ToString(Formatting.None)) < ResponseSizeGuard.EnforcementBudgetBytes);
        }

        [Fact]
        public void Send_code_auto_spills_arbitrary_output_above_budget_without_false_failure()
        {
            var output = new string('x', ResponseSizeGuard.EnforcementBudgetBytes + 1000);
            var data = new JObject { ["executed"] = true, ["result"] = output };
            var rawResponse = JsonConvert.SerializeObject(new { success = true, data });
            var outcome = new ResponseSpillProcessor(new ResponseSpillWriter(_directory)).Process(
                "send_code_to_revit",
                "{\"code\":\"return value;\"}",
                true,
                data,
                rawResponse);

            Assert.True(outcome.Spilled);
            Assert.True(outcome.Automatic);
            var envelope = Assert.IsType<JObject>(outcome.Data);
            Assert.True(envelope.Value<bool>("success"));
            Assert.True(envelope.Value<bool>("automatic"));
            Assert.Equal("text", envelope.Value<string>("format"));
            Assert.Equal(output, File.ReadAllText(envelope.Value<string>("path")!, Encoding.UTF8));
            Assert.DoesNotContain("success=false", envelope.ToString(Formatting.None));
        }

        [Fact]
        public void Spill_write_failure_preserves_completed_batch_mutation_truthfully()
        {
            File.WriteAllText(_directory, "not a directory");
            var data = new JObject { ["rolledBack"] = false, ["results"] = new JArray(1, 2) };
            var rawResponse = JsonConvert.SerializeObject(new { success = true, data });

            var outcome = new ResponseSpillProcessor(new ResponseSpillWriter(_directory)).Process(
                "batch_execute",
                "{\"output\":\"file\"}",
                true,
                data,
                rawResponse);

            Assert.False(outcome.Spilled);
            var envelope = Assert.IsType<JObject>(outcome.Data);
            Assert.False(envelope.Value<bool>("spill_succeeded"));
            Assert.True(envelope.Value<bool>("operation_completed"));
            Assert.True(envelope.Value<bool>("mutation_applied"));
        }

        [Fact]
        public void Spill_write_failure_does_not_claim_dry_run_was_applied()
        {
            File.WriteAllText(_directory, "not a directory");
            var data = new JObject { ["dry_run"] = true, ["changed_elements"] = new JArray(1, 2) };
            var rawResponse = JsonConvert.SerializeObject(new { success = true, data });

            var outcome = new ResponseSpillProcessor(new ResponseSpillWriter(_directory)).Process(
                "workflow_data_roundtrip",
                "{\"output\":\"file\",\"dry_run\":true}",
                true,
                data,
                rawResponse);

            var envelope = Assert.IsType<JObject>(outcome.Data);
            Assert.False(envelope.Value<bool>("mutation_applied"));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
                else if (File.Exists(_directory)) File.Delete(_directory);
            }
            catch { }
        }
    }
}
