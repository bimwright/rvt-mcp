using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetMaterialAppearanceHandler : IRevitCommand
    {
        public string Name => "set_material_appearance";
        public string Description => "Set shading, transparency, and pattern assets for a material";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""material_id"": { ""type"": ""integer"" },
    ""material_name"": { ""type"": ""string"" },
    ""red"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 },
    ""green"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 },
    ""blue"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 },
    ""transparency"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 100 },
    ""shininess"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 128 },
    ""smoothness"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 128 },
    ""use_render_appearance_for_shading"": { ""type"": ""boolean"" },
    ""surface_foreground_pattern_id"": { ""type"": ""integer"" },
    ""surface_background_pattern_id"": { ""type"": ""integer"" },
    ""cut_foreground_pattern_id"": { ""type"": ""integer"" },
    ""cut_background_pattern_id"": { ""type"": ""integer"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var materialId = request.Value<long?>("material_id");
            var materialName = request.Value<string>("material_name") ?? "";

            Material mat = null;
            var allMats = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .ToList();

            if (materialId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(materialId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(materialId.Value));

                var elId = RevitCompat.ToElementId(materialId.Value);
                mat = doc.GetElement(elId) as Material;
                if (mat == null)
                    return CommandResult.Fail($"Material with ID {materialId} not found.");
            }
            else if (!string.IsNullOrEmpty(materialName))
            {
                var matching = allMats
                    .Where(m => m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matching.Count == 0)
                    return CommandResult.Fail($"Material with name '{materialName}' not found.");
                if (matching.Count > 1)
                    return CommandResult.Fail($"Multiple materials found with name '{materialName}'. Please use material_id.");

                mat = matching[0];
            }
            else
            {
                return CommandResult.Fail("Either material_id or material_name must be supplied.");
            }

            // Color preflight
            var redToken = request["red"];
            var greenToken = request["green"];
            var blueToken = request["blue"];
            bool hasRgb = (redToken != null || greenToken != null || blueToken != null);
            int? red = null, green = null, blue = null;
            if (hasRgb)
            {
                if (redToken == null || greenToken == null || blueToken == null)
                    return CommandResult.Fail("All red, green, and blue values must be provided to set a color.");

                red = redToken.Value<int>();
                green = greenToken.Value<int>();
                blue = blueToken.Value<int>();
                if (red < 0 || red > 255 || green < 0 || green > 255 || blue < 0 || blue > 255)
                    return CommandResult.Fail("Color components must be between 0 and 255.");
            }

            // Transparency preflight
            var transparencyToken = request["transparency"];
            int? transparency = null;
            if (transparencyToken != null && transparencyToken.Type != JTokenType.Null)
            {
                transparency = transparencyToken.Value<int>();
                if (transparency < 0 || transparency > 100)
                    return CommandResult.Fail("Transparency must be between 0 and 100.");
            }

            // Shininess/Smoothness preflight
            var shininessToken = request["shininess"];
            int? shininess = null;
            if (shininessToken != null && shininessToken.Type != JTokenType.Null)
            {
                shininess = shininessToken.Value<int>();
                if (shininess < 0 || shininess > 128)
                    return CommandResult.Fail("Shininess must be between 0 and 128.");
            }

            var smoothnessToken = request["smoothness"];
            int? smoothness = null;
            if (smoothnessToken != null && smoothnessToken.Type != JTokenType.Null)
            {
                smoothness = smoothnessToken.Value<int>();
                if (smoothness < 0 || smoothness > 128)
                    return CommandResult.Fail("Smoothness must be between 0 and 128.");
            }

            var useRenderAppearanceToken = request["use_render_appearance_for_shading"];
            bool? useRenderAppearance = null;
            if (useRenderAppearanceToken != null && useRenderAppearanceToken.Type != JTokenType.Null)
            {
                useRenderAppearance = useRenderAppearanceToken.Value<bool>();
            }

            // Patterns preflight
            long? surfaceFgId = request.Value<long?>("surface_foreground_pattern_id");
            long? surfaceBgId = request.Value<long?>("surface_background_pattern_id");
            long? cutFgId = request.Value<long?>("cut_foreground_pattern_id");
            long? cutBgId = request.Value<long?>("cut_background_pattern_id");

            // Verify patterns
            FillPatternElement sfElem = null, sbElem = null, cfElem = null, cbElem = null;

            if (surfaceFgId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(surfaceFgId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(surfaceFgId.Value));

                sfElem = doc.GetElement(RevitCompat.ToElementId(surfaceFgId.Value)) as FillPatternElement;
                if (sfElem == null)
                    return CommandResult.Fail($"Surface foreground pattern ID {surfaceFgId.Value} is not a valid FillPatternElement.");
            }
            if (surfaceBgId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(surfaceBgId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(surfaceBgId.Value));

                sbElem = doc.GetElement(RevitCompat.ToElementId(surfaceBgId.Value)) as FillPatternElement;
                if (sbElem == null)
                    return CommandResult.Fail($"Surface background pattern ID {surfaceBgId.Value} is not a valid FillPatternElement.");
            }
            if (cutFgId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(cutFgId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(cutFgId.Value));

                cfElem = doc.GetElement(RevitCompat.ToElementId(cutFgId.Value)) as FillPatternElement;
                if (cfElem == null)
                    return CommandResult.Fail($"Cut foreground pattern ID {cutFgId.Value} is not a valid FillPatternElement.");
                if (cfElem.GetFillPattern().Target == FillPatternTarget.Model)
                    return CommandResult.Fail($"Cut foreground pattern '{cfElem.Name}' must be a Drafting pattern, but it is a Model pattern.");
            }
            if (cutBgId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(cutBgId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(cutBgId.Value));

                cbElem = doc.GetElement(RevitCompat.ToElementId(cutBgId.Value)) as FillPatternElement;
                if (cbElem == null)
                    return CommandResult.Fail($"Cut background pattern ID {cutBgId.Value} is not a valid FillPatternElement.");
                if (cbElem.GetFillPattern().Target == FillPatternTarget.Model)
                    return CommandResult.Fail($"Cut background pattern '{cbElem.Name}' must be a Drafting pattern, but it is a Model pattern.");
            }

            // Check if there are any fields to update
            bool hasUpdates = hasRgb || transparency.HasValue || shininess.HasValue || smoothness.HasValue ||
                              useRenderAppearance.HasValue || surfaceFgId.HasValue || surfaceBgId.HasValue ||
                              cutFgId.HasValue || cutBgId.HasValue;

            if (!hasUpdates)
                return CommandResult.Fail("No update fields were provided.");

            var changed = new Dictionary<string, string>();

            using (var tx = new Transaction(doc, "RvtMcp: set material appearance"))
            {
                tx.Start();
                try
                {
                    if (hasRgb)
                    {
                        mat.Color = new Color((byte)red.Value, (byte)green.Value, (byte)blue.Value);
                        changed["color"] = "set";
                    }
                    if (transparency.HasValue)
                    {
                        mat.Transparency = transparency.Value;
                        changed["transparency"] = "set";
                    }
                    if (shininess.HasValue)
                    {
                        mat.Shininess = shininess.Value;
                        changed["shininess"] = "set";
                    }
                    if (smoothness.HasValue)
                    {
                        mat.Smoothness = smoothness.Value;
                        changed["smoothness"] = "set";
                    }
                    if (useRenderAppearance.HasValue)
                    {
                        mat.UseRenderAppearanceForShading = useRenderAppearance.Value;
                        changed["use_render_appearance_for_shading"] = "set";
                    }
                    if (sfElem != null)
                    {
                        mat.SurfaceForegroundPatternId = sfElem.Id;
                        changed["surface_foreground_pattern_id"] = "set";
                    }
                    if (sbElem != null)
                    {
                        mat.SurfaceBackgroundPatternId = sbElem.Id;
                        changed["surface_background_pattern_id"] = "set";
                    }
                    if (cfElem != null)
                    {
                        mat.CutForegroundPatternId = cfElem.Id;
                        changed["cut_foreground_pattern_id"] = "set";
                    }
                    if (cbElem != null)
                    {
                        mat.CutBackgroundPatternId = cbElem.Id;
                        changed["cut_background_pattern_id"] = "set";
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {status}.");

                    return CommandResult.Ok(new
                    {
                        updated = true,
                        material_id = RevitCompat.GetId(mat.Id),
                        name = mat.Name,
                        changed,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to set material appearance: " + ex.Message);
                }
            }
        }
    }
}
