using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class JsonInputSerializationTests
    {
        [Theory]
        [InlineData("dimensions", "references")]
        [InlineData("filled_region", "points")]
        [InlineData("room_separator", "points")]
        public async Task Sdk_bound_object_arrays_preserve_each_json_object(string tool, string field)
        {
            const string json = "[{\"element_id\":42,\"mode\":\"center\",\"x\":12.5,\"y\":-20,\"z\":0},{\"element_id\":43,\"x\":100,\"y\":30,\"z\":0}]";
            var values = JsonSerializer.Deserialize<object[]>(json);
            var sent = await Capture.Send(() => tool switch
            {
                "dimensions" => AnnotationTools.CreateDimensions(values),
                "filled_region" => AnnotationTools.CreateFilledRegion(values),
                _ => RoomsTools.CreateRoomSeparator(values)
            });

            Assert.True(JToken.DeepEquals(JArray.Parse(json), sent.Json()[field]), sent.Json().ToString());
        }

        [Fact]
        public async Task Mixed_json_and_native_points_preserve_nested_values_and_nulls()
        {
            var point = JsonSerializer.Deserialize<JsonElement>("{\"x\":1,\"y\":2,\"meta\":{\"label\":\"Phòng\",\"values\":[true,null,3]}}");
            var values = new object[] { point, new { x = 3, y = 4 }, JObject.Parse("{\"x\":5,\"y\":6}") };
            var sent = await Capture.Send(() => AnnotationTools.CreateFilledRegion(values));
            var actual = sent.Json()["points"];

            Assert.Equal(1, actual[0].Value<int>("x"));
            Assert.Equal("Phòng", actual[0]["meta"].Value<string>("label"));
            Assert.True(actual[0]["meta"]["values"][0].Value<bool>());
            Assert.Equal(JTokenType.Null, actual[0]["meta"]["values"][1].Type);
            Assert.Equal(3, actual[1].Value<int>("x"));
            Assert.Equal(6, actual[2].Value<int>("y"));
        }

        [Fact]
        public async Task Sheet_items_preserve_ids_and_new_numbers()
        {
            const string json = "[{\"sheet_id\":42,\"new_number\":\"A-02\"}]";
            var sent = await Capture.Send(() => SheetsTools.RenumberSheets(JsonSerializer.Deserialize<object[]>(json)));

            Assert.True(JToken.DeepEquals(JArray.Parse(json), sent.Json()["items"]), sent.Json().ToString());
        }

        [Fact]
        public async Task Native_volume_with_json_children_preserves_coordinates()
        {
            var volume = new
            {
                min = JsonSerializer.Deserialize<JsonElement>("{\"x\":-10,\"y\":-20,\"z\":0}"),
                max = JsonSerializer.Deserialize<JsonElement>("{\"x\":100,\"y\":200,\"z\":300}")
            };
            var sent = await Capture.Send(() => GeometryTools.FindElementsInVolume(volume));

            Assert.Equal(-10, sent.Json()["volume"]["min"].Value<int>("x"));
            Assert.Equal(300, sent.Json()["volume"]["max"].Value<int>("z"));
        }

        [Fact]
        public void Baked_tool_dictionary_preserves_nested_json_arguments()
        {
            const string json = "[{\"x\":1,\"y\":2}]";
            var args = new Dictionary<string, object> { ["points"] = JsonSerializer.Deserialize<JsonElement>(json) };
            var actual = ToolbakerTools.NormalizeRunBakedToolParams(args);

            Assert.True(JToken.DeepEquals(JArray.Parse(json), actual["points"]), actual.ToString());
        }
    }
}
