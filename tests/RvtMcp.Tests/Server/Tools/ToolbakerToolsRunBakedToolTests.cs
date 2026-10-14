using System;
using System.Text.Json;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolbakerToolsRunBakedToolTests
    {
        private const string RunBakedToolSchema = @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""params"":{""type"":""object""}},""required"":[""name""]}";

        [Fact]
        public void NormalizeRunBakedToolParams_SerializesObjectParamsForPluginSchema()
        {
            using var doc = JsonDocument.Parse(@"{""height"":3000,""label"":""A""}");

            var normalized = ToolbakerTools.NormalizeRunBakedToolParams(doc.RootElement);
            var payload = JsonConvert.SerializeObject(new { name = "custom_baked_command", @params = normalized });

            Assert.Contains(@"""params"":{", payload);
            Assert.DoesNotContain(@"""params"":""", payload);
            var validation = SchemaValidator.Validate(RunBakedToolSchema, payload);
            Assert.True(validation.IsValid, validation.Error);
        }

        [Fact]
        public void NormalizeRunBakedToolParams_DefaultsToEmptyObject()
        {
            var normalized = ToolbakerTools.NormalizeRunBakedToolParams(null);

            Assert.Equal(JTokenType.Object, normalized.Type);
            Assert.Empty(normalized.Properties());
        }

        [Fact]
        public void NormalizeRunBakedToolParams_AcceptsJObject()
        {
            var source = new JObject
            {
                ["height"] = 3000,
                ["label"] = "A",
            };

            var normalized = ToolbakerTools.NormalizeRunBakedToolParams(source);

            Assert.Same(source, normalized);
        }

        [Fact]
        public void NormalizeRunBakedToolParams_AcceptsClrObject()
        {
            var normalized = ToolbakerTools.NormalizeRunBakedToolParams(new { height = 3000, label = "A" });

            Assert.Equal(3000, normalized.Value<int>("height"));
            Assert.Equal("A", normalized.Value<string>("label"));
        }

        [Fact]
        public void NormalizeRunBakedToolParams_AcceptsJsonElementNullAsEmptyObject()
        {
            using var doc = JsonDocument.Parse("null");

            var normalized = ToolbakerTools.NormalizeRunBakedToolParams(doc.RootElement);

            Assert.Equal(JTokenType.Object, normalized.Type);
            Assert.Empty(normalized.Properties());
        }

        [Fact]
        public void NormalizeRunBakedToolParams_RejectsJsonElementArray()
        {
            using var doc = JsonDocument.Parse(@"[1,2,3]");

            var ex = Assert.Throws<ArgumentException>(() =>
                ToolbakerTools.NormalizeRunBakedToolParams(doc.RootElement));

            Assert.Equal("params must be a JSON object.", ex.Message);
        }

        [Fact]
        public void NormalizeRunBakedToolParams_RejectsJsonElementString()
        {
            using var doc = JsonDocument.Parse(@"""not-object""");

            var ex = Assert.Throws<ArgumentException>(() =>
                ToolbakerTools.NormalizeRunBakedToolParams(doc.RootElement));

            Assert.Equal("params must be a JSON object.", ex.Message);
        }

        [Fact]
        public void NormalizeRunBakedToolParams_RejectsJsonElementNumber()
        {
            using var doc = JsonDocument.Parse("42");

            var ex = Assert.Throws<ArgumentException>(() =>
                ToolbakerTools.NormalizeRunBakedToolParams(doc.RootElement));

            Assert.Equal("params must be a JSON object.", ex.Message);
        }

        [Fact]
        public void NormalizeRunBakedToolParams_RejectsNonObjectJToken()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                ToolbakerTools.NormalizeRunBakedToolParams(new JArray(1, 2, 3)));

            Assert.Equal("params must be a JSON object.", ex.Message);
        }

        [Fact]
        public void NormalizeRunBakedToolParams_RejectsNonObjectClrValue()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                ToolbakerTools.NormalizeRunBakedToolParams("not-object"));

            Assert.Equal("params must be a JSON object.", ex.Message);
        }
    }
}
