using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class GeometryToolShellTests
    {
        private static readonly string[] GeometryToolsInClass =
        {
            "revit_analyze_geometry_complexity",
            "revit_clash_detection",
            "revit_compute_element_area",
            "revit_compute_element_volume",
            "revit_find_elements_in_volume",
            "revit_find_overlapping_elements",
            "revit_get_element_bounding_box",
            "revit_get_element_centroid",
            "revit_get_element_geometry",
            "revit_measure_distance_between_elements",
            "revit_project_point_onto_face",
            "revit_raycast_from_point"
        };

        [Fact]
        public void Every_geometry_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(GeometryTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(GeometryToolsInClass, declared);
        }

        [Fact]
        public async Task Get_element_bounding_box_sends_ids_and_view()
        {
            var sent = await Capture.Send(() => GeometryTools.GetElementBoundingBox(new long[] { 1, 2 }, 99, true));

            Assert.Equal("get_element_bounding_box", sent.Command);
            var json = sent.Json();
            Assert.Equal(new[] { 1L, 2L }, json["element_ids"].Select(id => id.Value<long>()).ToArray());
            Assert.Equal(99L, json.Value<long>("view_id"));
            Assert.True(json.Value<bool>("include_transform"));
        }

        [Fact]
        public async Task Get_element_geometry_sends_detail_and_sampling()
        {
            var sent = await Capture.Send(() => GeometryTools.GetElementGeometry(new long[] { 5 }, "Fine", true, 7));

            Assert.Equal("get_element_geometry", sent.Command);
            var json = sent.Json();
            Assert.Equal(5L, json["element_ids"][0].Value<long>());
            Assert.Equal("Fine", json.Value<string>("detail_level"));
            Assert.True(json.Value<bool>("include_samples"));
            Assert.Equal(7, json.Value<int>("sample_limit"));
        }

        [Fact]
        public async Task Measure_distance_sends_pair_and_strategy()
        {
            var sent = await Capture.Send(() => GeometryTools.MeasureDistanceBetweenElements(1, 2, "location"));

            Assert.Equal("measure_distance_between_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal(1L, json.Value<long>("element_id_1"));
            Assert.Equal(2L, json.Value<long>("element_id_2"));
            Assert.Equal("location", json.Value<string>("strategy"));
        }

        [Fact]
        public async Task Clash_detection_sends_categories_and_limits()
        {
            var sent = await Capture.Send(() => GeometryTools.ClashDetection(
                new[] { "Walls" }, new[] { "Floors" }, 42, "bbox", 50, 10));

            Assert.Equal("clash_detection", sent.Command);
            var json = sent.Json();
            Assert.Equal("Walls", json["categories_a"][0].Value<string>());
            Assert.Equal("Floors", json["categories_b"][0].Value<string>());
            Assert.Equal(42L, json.Value<long>("view_id"));
            Assert.Equal("bbox", json.Value<string>("strategy"));
            Assert.Equal(50, json.Value<int>("max_pairs"));
            Assert.Equal(10, json.Value<int>("max_results"));
        }

        [Fact]
        public async Task Raycast_sends_vector_and_view()
        {
            var sent = await Capture.Send(() => GeometryTools.RaycastFromPoint(
                1, 2, 3, 0, 0, -1, 933640, new[] { "Pipes" }, 500));

            Assert.Equal("raycast_from_point", sent.Command);
            var json = sent.Json();
            Assert.Equal(1.0, json.Value<double>("x"));
            Assert.Equal(-1.0, json.Value<double>("dir_z"));
            Assert.Equal(933640L, json.Value<long>("view_3d_id"));
            Assert.Equal("Pipes", json["categories"][0].Value<string>());
            Assert.Equal(500.0, json.Value<double>("max_distance"));
        }

        [Fact]
        public async Task Find_elements_in_volume_sends_volume_object()
        {
            using var doc = JsonDocument.Parse("{\"min\":{\"x\":1,\"y\":2,\"z\":3},\"max\":{\"x\":4,\"y\":5,\"z\":6}}");

            var sent = await Capture.Send(() => GeometryTools.FindElementsInVolume(
                doc.RootElement, null, new[] { "Pipes" }, 9, "inside", 25));

            Assert.Equal("find_elements_in_volume", sent.Command);
            var json = sent.Json();
            Assert.Equal(1.0, json["volume"]["min"].Value<double>("x"));
            Assert.Equal(6.0, json["volume"]["max"].Value<double>("z"));
            Assert.Equal("Pipes", json["categories"][0].Value<string>());
            Assert.Equal(9L, json.Value<long>("view_id"));
            Assert.Equal("inside", json.Value<string>("match"));
            Assert.Equal(25, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Find_elements_in_volume_omits_null_volume()
        {
            var sent = await Capture.Send(() => GeometryTools.FindElementsInVolume());

            Assert.Equal("find_elements_in_volume", sent.Command);
            Assert.Null(sent.Json().Property("volume"));
        }

        [Fact]
        public async Task Compute_element_volume_sends_ids_and_detail()
        {
            var sent = await Capture.Send(() => GeometryTools.ComputeElementVolume(new long[] { 8 }, "Fine"));

            Assert.Equal("compute_element_volume", sent.Command);
            var json = sent.Json();
            Assert.Equal(8L, json["element_ids"][0].Value<long>());
            Assert.Equal("Fine", json.Value<string>("detail_level"));
        }

        [Fact]
        public async Task Compute_element_area_sends_ids_and_detail()
        {
            var sent = await Capture.Send(() => GeometryTools.ComputeElementArea(new long[] { 8 }, "Coarse"));

            Assert.Equal("compute_element_area", sent.Command);
            var json = sent.Json();
            Assert.Equal(8L, json["element_ids"][0].Value<long>());
            Assert.Equal("Coarse", json.Value<string>("detail_level"));
        }

        [Fact]
        public async Task Project_point_onto_face_sends_point()
        {
            var sent = await Capture.Send(() => GeometryTools.ProjectPointOntoFace(7, 1.5, 2.5, 3.5, 2, "Fine"));

            Assert.Equal("project_point_onto_face", sent.Command);
            var json = sent.Json();
            Assert.Equal(7L, json.Value<long>("element_id"));
            Assert.Equal(1.5, json.Value<double>("x"));
            Assert.Equal(3.5, json.Value<double>("z"));
            Assert.Equal(2, json.Value<int>("face_index"));
            Assert.Equal("Fine", json.Value<string>("detail_level"));
        }

        [Fact]
        public async Task Find_overlapping_elements_sends_category_and_limits()
        {
            var sent = await Capture.Send(() => GeometryTools.FindOverlappingElements("Pipes", 5, 200, 40));

            Assert.Equal("find_overlapping_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal("Pipes", json.Value<string>("category"));
            Assert.Equal(5L, json.Value<long>("view_id"));
            Assert.Equal(200, json.Value<int>("max_pairs"));
            Assert.Equal(40, json.Value<int>("max_results"));
        }

        [Fact]
        public async Task Get_element_centroid_sends_ids_and_strategy()
        {
            var sent = await Capture.Send(() => GeometryTools.GetElementCentroid(new long[] { 3 }, "bbox"));

            Assert.Equal("get_element_centroid", sent.Command);
            var json = sent.Json();
            Assert.Equal(3L, json["element_ids"][0].Value<long>());
            Assert.Equal("bbox", json.Value<string>("strategy"));
        }

        [Fact]
        public async Task Analyze_geometry_complexity_sends_scope()
        {
            var sent = await Capture.Send(() => GeometryTools.AnalyzeGeometryComplexity(
                new long[] { 1 }, new[] { "Pipes" }, 9, "Fine", 30));

            Assert.Equal("analyze_geometry_complexity", sent.Command);
            var json = sent.Json();
            Assert.Equal(1L, json["element_ids"][0].Value<long>());
            Assert.Equal("Pipes", json["categories"][0].Value<string>());
            Assert.Equal(9L, json.Value<long>("view_id"));
            Assert.Equal("Fine", json.Value<string>("detail_level"));
            Assert.Equal(30, json.Value<int>("limit"));
        }
    }
}
