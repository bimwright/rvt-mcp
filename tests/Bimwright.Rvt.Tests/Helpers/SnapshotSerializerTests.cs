using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bimwright.Rvt.Tests.Helpers
{
    public class SnapshotSerializerTests
    {
        [Fact]
        public void Hash_same_text_produces_same_hash()
        {
            Assert.Equal(
                SnapshotSerializer.HashDescription("hello"),
                SnapshotSerializer.HashDescription("hello"));
        }

        [Fact]
        public void Hash_different_text_produces_different_hash()
        {
            Assert.NotEqual(
                SnapshotSerializer.HashDescription("hello"),
                SnapshotSerializer.HashDescription("world"));
        }

        [Fact]
        public void Hash_format_is_sha256_prefix()
        {
            var hash = SnapshotSerializer.HashDescription("hello");
            Assert.StartsWith("sha256:", hash);
            // sha256: + 64 hex chars = 71 chars
            Assert.Equal(71, hash.Length);
        }

        [Fact]
        public void Serialize_sorts_tool_array_by_name_ascending()
        {
            var tools = new[]
            {
                new { name = "zebra", description_hash = "sha256:0", inputSchema = new { } },
                new { name = "alpha", description_hash = "sha256:1", inputSchema = new { } }
            };
            var json = SnapshotSerializer.Serialize(toolCount: 2, tools: tools);
            var parsed = JObject.Parse(json);
            var names = parsed["tools"]!.Select(t => t["name"]!.ToString()).ToArray();
            Assert.Equal(new[] { "alpha", "zebra" }, names);
        }

        [Fact]
        public void Serialize_output_is_stable_across_runs()
        {
            var tools = new[] { new { name = "x", description_hash = "sha256:0", inputSchema = new { } } };
            var a = SnapshotSerializer.Serialize(toolCount: 1, tools: tools);
            var b = SnapshotSerializer.Serialize(toolCount: 1, tools: tools);
            Assert.Equal(a, b);
        }
    }
}
