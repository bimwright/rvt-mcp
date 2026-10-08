using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using RvtMcp.Server.Prompts;
using RvtMcp.ToolCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("materials")]
    public class MaterialsTools
    {
        [McpServerTool(Name = "revit_list_materials", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("List and filter materials in the active document")]
        public static async Task<string> ListMaterials(string namePattern = "", string classFilter = "", bool includeAssets = true, bool includeUseCount = false, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_materials", new
                {
                    name_pattern = namePattern,
                    class_filter = classFilter,
                    include_assets = includeAssets,
                    include_use_count = includeUseCount,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_material_properties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Get detailed properties of a material by ID or name")]
        public static async Task<string> GetMaterialProperties(long? materialId = null, string materialName = "", bool includeAssets = true, bool includeParameters = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_material_properties", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    include_assets = includeAssets,
                    include_parameters = includeParameters
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_material", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Create a new material with optional graphics parameters")]
        public static async Task<string> CreateMaterial(string name, string materialClass = "", string materialCategory = "", int? red = null, int? green = null, int? blue = null, int? transparency = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_material", new
                {
                    name,
                    material_class = materialClass,
                    material_category = materialCategory,
                    red,
                    green,
                    blue,
                    transparency
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_duplicate_material", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Duplicate an existing material with a new name")]
        public static async Task<string> DuplicateMaterial(string newName, long? sourceMaterialId = null, string sourceMaterialName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("duplicate_material", new
                {
                    new_name = newName,
                    source_material_id = sourceMaterialId,
                    source_material_name = sourceMaterialName
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_material_appearance", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace material graphics; restore them with Revit Undo. Set shading, transparency, and pattern assets for a material")]
        public static async Task<string> SetMaterialAppearance(long? materialId = null, string materialName = "", int? red = null, int? green = null, int? blue = null, int? transparency = null, int? shininess = null, int? smoothness = null, bool? useRenderAppearanceForShading = null, long? surfaceForegroundPatternId = null, long? surfaceBackgroundPatternId = null, long? cutForegroundPatternId = null, long? cutBackgroundPatternId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_material_appearance", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    red,
                    green,
                    blue,
                    transparency,
                    shininess,
                    smoothness,
                    use_render_appearance_for_shading = useRenderAppearanceForShading,
                    surface_foreground_pattern_id = surfaceForegroundPatternId,
                    surface_background_pattern_id = surfaceBackgroundPatternId,
                    cut_foreground_pattern_id = cutForegroundPatternId,
                    cut_background_pattern_id = cutBackgroundPatternId
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_material_identity", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace material identity values; restore them with Revit Undo. Set identity parameters for a material")]
        public static async Task<string> SetMaterialIdentity(long? materialId = null, string materialName = "", string manufacturer = null, string model = null, string cost = null, string keynote = null, string mark = null, string url = null, string materialClass = null, string materialCategory = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_material_identity", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    manufacturer,
                    model,
                    cost,
                    keynote,
                    mark,
                    url,
                    material_class = materialClass,
                    material_category = materialCategory
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_material_structural_asset", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace material structural properties; restore them with Revit Undo. Set or create a structural physical property asset for a material")]
        public static async Task<string> SetMaterialStructuralAsset(long? materialId = null, string materialName = "", string assetName = "", string structuralClass = "generic", double? densityKgPerM3 = null, double? youngModulusMpa = null, double? poissonRatio = null, double? shearModulusMpa = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_material_structural_asset", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    asset_name = assetName,
                    structural_class = structuralClass,
                    density_kg_per_m3 = densityKgPerM3,
                    young_modulus_mpa = youngModulusMpa,
                    poisson_ratio = poissonRatio,
                    shear_modulus_mpa = shearModulusMpa
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_material_thermal_asset", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace material thermal properties; restore them with Revit Undo. Set or create a thermal property asset for a material")]
        public static async Task<string> SetMaterialThermalAsset(long? materialId = null, string materialName = "", string assetName = "", double? conductivityWPerMK = null, double? specificHeatJPerKgK = null, double? emissivity = null, double? permeability = null, double? densityKgPerM3 = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_material_thermal_asset", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    asset_name = assetName,
                    conductivity_w_per_m_k = conductivityWPerMK,
                    specific_heat_j_per_kg_k = specificHeatJPerKgK,
                    emissivity,
                    permeability,
                    density_kg_per_m3 = densityKgPerM3
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_assign_material_to_element", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace element or type material assignments; restore them with Revit Undo. Assign a material to one or more elements, optionally specifying parameter name or compound layer index")]
        public static async Task<string> AssignMaterialToElement(long[] elementIds, long? materialId = null, string materialName = "", string parameterName = "", int? compoundLayerIndex = null, bool allowTypeMutation = false, string duplicateTypeName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("assign_material_to_element", new
                {
                    element_ids = elementIds,
                    material_id = materialId,
                    material_name = materialName,
                    parameter_name = parameterName,
                    compound_layer_index = compoundLayerIndex,
                    allow_type_mutation = allowTypeMutation,
                    duplicate_type_name = duplicateTypeName
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_material_takeoff", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Calculate material takeoff. output=file writes SQLite to a local same-machine absolute path with schema and bounded preview; remote clients receive preview only.")]
        public static async Task<string> GetMaterialTakeoff(string categoryFilter = "", string materialNamePattern = "", bool includeElements = false, int elementLimit = 100, string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_material_takeoff", new
                {
                    category_filter = categoryFilter,
                    material_name_pattern = materialNamePattern,
                    include_elements = includeElements,
                    element_limit = elementLimit,
                    output
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
