using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class MaterialsToolShellTests
    {
        private static readonly string[] MaterialsToolsInClass =
        {
            "revit_assign_material_to_element",
            "revit_create_material",
            "revit_duplicate_material",
            "revit_get_material_properties",
            "revit_get_material_takeoff",
            "revit_list_materials",
            "revit_set_material_appearance",
            "revit_set_material_identity",
            "revit_set_material_structural_asset",
            "revit_set_material_thermal_asset"
        };

        [Fact]
        public void Every_materials_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(MaterialsTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(MaterialsToolsInClass, declared);
        }

        [Fact]
        public async Task List_materials_sends_snake_case_filters()
        {
            var sent = await Capture.Send(() => MaterialsTools.ListMaterials("Concrete", "Concrete", false, true, 10));

            Assert.Equal("list_materials", sent.Command);
            var json = sent.Json();
            Assert.Equal("Concrete", json.Value<string>("name_pattern"));
            Assert.Equal("Concrete", json.Value<string>("class_filter"));
            Assert.False(json.Value<bool>("include_assets"));
            Assert.True(json.Value<bool>("include_use_count"));
            Assert.Equal(10, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Get_material_properties_sends_id_and_flags()
        {
            var sent = await Capture.Send(() => MaterialsTools.GetMaterialProperties(523, "", true, false));

            Assert.Equal("get_material_properties", sent.Command);
            var json = sent.Json();
            Assert.Equal(523, json.Value<long>("material_id"));
            Assert.True(json.Value<bool>("include_assets"));
            Assert.False(json.Value<bool>("include_parameters"));
        }

        [Fact]
        public async Task Create_material_sends_graphics()
        {
            var sent = await Capture.Send(() => MaterialsTools.CreateMaterial("M1", "Metal", "Metal", 10, 20, 30, 50));

            Assert.Equal("create_material", sent.Command);
            var json = sent.Json();
            Assert.Equal("M1", json.Value<string>("name"));
            Assert.Equal("Metal", json.Value<string>("material_class"));
            Assert.Equal(10, json.Value<int>("red"));
            Assert.Equal(30, json.Value<int>("blue"));
            Assert.Equal(50, json.Value<int>("transparency"));
        }

        [Fact]
        public async Task Duplicate_material_sends_source_selector()
        {
            var sent = await Capture.Send(() => MaterialsTools.DuplicateMaterial("Copy", 7, ""));

            Assert.Equal("duplicate_material", sent.Command);
            var json = sent.Json();
            Assert.Equal("Copy", json.Value<string>("new_name"));
            Assert.Equal(7, json.Value<long>("source_material_id"));
        }

        [Fact]
        public async Task Set_material_appearance_sends_pattern_ids()
        {
            var sent = await Capture.Send(() => MaterialsTools.SetMaterialAppearance(
                5, "", 1, 2, 3, 4, 5, 6, true, 11, 12, 13, 14));

            Assert.Equal("set_material_appearance", sent.Command);
            var json = sent.Json();
            Assert.Equal(5, json.Value<long>("material_id"));
            Assert.Equal(11, json.Value<long>("surface_foreground_pattern_id"));
            Assert.Equal(14, json.Value<long>("cut_background_pattern_id"));
            Assert.True(json.Value<bool>("use_render_appearance_for_shading"));
        }

        [Fact]
        public async Task Set_material_identity_sends_fields()
        {
            var sent = await Capture.Send(() => MaterialsTools.SetMaterialIdentity(
                5, "", "manu", "model", "9.99", "key", "mark", "http://u", "cls", "cat"));

            Assert.Equal("set_material_identity", sent.Command);
            var json = sent.Json();
            Assert.Equal(5, json.Value<long>("material_id"));
            Assert.Equal("manu", json.Value<string>("manufacturer"));
            Assert.Equal("9.99", json.Value<string>("cost"));
            Assert.Equal("key", json.Value<string>("keynote"));
            Assert.Equal("cls", json.Value<string>("material_class"));
        }

        [Fact]
        public async Task Set_material_structural_asset_sends_values()
        {
            var sent = await Capture.Send(() => MaterialsTools.SetMaterialStructuralAsset(
                5, "", "a", "concrete", 2400, 20500, 0.2, 9430));

            Assert.Equal("set_material_structural_asset", sent.Command);
            var json = sent.Json();
            Assert.Equal(5, json.Value<long>("material_id"));
            Assert.Equal("concrete", json.Value<string>("structural_class"));
            Assert.Equal(2400, json.Value<double>("density_kg_per_m3"));
            Assert.Equal(20500, json.Value<double>("young_modulus_mpa"));
            Assert.Equal(0.2, json.Value<double>("poisson_ratio"));
            Assert.Equal(9430, json.Value<double>("shear_modulus_mpa"));
        }

        [Fact]
        public async Task Set_material_thermal_asset_sends_values()
        {
            var sent = await Capture.Send(() => MaterialsTools.SetMaterialThermalAsset(
                5, "", "a", 1.046, 657, 0.95, 0, 2300));

            Assert.Equal("set_material_thermal_asset", sent.Command);
            var json = sent.Json();
            Assert.Equal(1.046, json.Value<double>("conductivity_w_per_m_k"));
            Assert.Equal(657, json.Value<double>("specific_heat_j_per_kg_k"));
            Assert.Equal(0.95, json.Value<double>("emissivity"));
            Assert.Equal(2300, json.Value<double>("density_kg_per_m3"));
        }

        [Fact]
        public async Task Assign_material_to_element_sends_ids_and_flags()
        {
            var sent = await Capture.Send(() => MaterialsTools.AssignMaterialToElement(
                new long[] { 1, 2 }, 5, "", "Material", 0, true, "dup"));

            Assert.Equal("assign_material_to_element", sent.Command);
            var json = sent.Json();
            Assert.Equal(2, json["element_ids"].Count());
            Assert.Equal(5, json.Value<long>("material_id"));
            Assert.Equal("Material", json.Value<string>("parameter_name"));
            Assert.Equal(0, json.Value<int>("compound_layer_index"));
            Assert.True(json.Value<bool>("allow_type_mutation"));
            Assert.Equal("dup", json.Value<string>("duplicate_type_name"));
        }

        [Fact]
        public async Task Get_material_takeoff_sends_scope_and_output()
        {
            var sent = await Capture.Send(() => MaterialsTools.GetMaterialTakeoff("Walls", "Conc", true, 50, "file"));

            Assert.Equal("get_material_takeoff", sent.Command);
            var json = sent.Json();
            Assert.Equal("Walls", json.Value<string>("category_filter"));
            Assert.Equal("Conc", json.Value<string>("material_name_pattern"));
            Assert.True(json.Value<bool>("include_elements"));
            Assert.Equal(50, json.Value<int>("element_limit"));
            Assert.Equal("file", json.Value<string>("output"));
        }
    }
}
