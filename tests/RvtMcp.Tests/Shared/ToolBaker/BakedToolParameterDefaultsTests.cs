using RvtMcp.Plugin.ToolBaker;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class ApplyBakeParameterDefaultsTests
    {
        [Fact]
        public void BuildDummyParams_uses_schema_types_and_defaults()
        {
            var schema = @"{
              ""type"": ""object"",
              ""properties"": {
                ""name"": { ""type"": ""string"", ""default"": ""Level 02"" },
                ""elevation"": { ""type"": ""number"" },
                ""count"": { ""type"": ""integer"" },
                ""enabled"": { ""type"": ""boolean"" },
                ""ids"": { ""type"": ""array"" },
                ""options"": { ""type"": ""object"" }
              }
            }";

            var json = BakedToolParameterDefaults.BuildDummyParamsJson(schema);
            var root = JObject.Parse(json);

            Assert.Equal("Level 02", (string)root["name"]);
            Assert.Equal(0, (int)root["elevation"]);
            Assert.Equal(0, (int)root["count"]);
            Assert.False((bool)root["enabled"]);
            Assert.Empty((JArray)root["ids"]);
            Assert.Empty((JObject)root["options"]);
        }

        [Fact]
        public void BuildDummyParams_invalid_schema_returns_empty_object()
        {
            Assert.Equal("{}", BakedToolParameterDefaults.BuildDummyParamsJson("not-json"));
        }
    }
}
