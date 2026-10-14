using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateMaterialHandler : IRevitCommand
    {
        public string Name => "create_material";
        public string Description => "Create a new material with optional graphics parameters";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""name""],
  ""properties"": {
    ""name"": { ""type"": ""string"" },
    ""material_class"": { ""type"": ""string"" },
    ""material_category"": { ""type"": ""string"" },
    ""red"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 },
    ""green"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 },
    ""blue"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 },
    ""transparency"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 100 }
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

            var name = request.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.Fail("Material name is required.");

            // Preflight duplicate material name with exact ordinal match
            var allMats = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .ToList();
            if (allMats.Any(m => m.Name.Equals(name, StringComparison.Ordinal)))
                return CommandResult.Fail($"A material named '{name}' already exists.");

            // Validation of red, green, blue
            var redToken = request["red"];
            var greenToken = request["green"];
            var blueToken = request["blue"];
            bool hasRgb = (redToken != null || greenToken != null || blueToken != null);
            int? red = null, green = null, blue = null;
            if (hasRgb)
            {
                if (redToken == null || greenToken == null || blueToken == null)
                    return CommandResult.Fail("All red, green, and blue values must be provided if setting a color.");

                red = redToken.Value<int>();
                green = greenToken.Value<int>();
                blue = blueToken.Value<int>();
                if (red < 0 || red > 255 || green < 0 || green > 255 || blue < 0 || blue > 255)
                    return CommandResult.Fail("Color components must be between 0 and 255.");
            }

            // Validation of transparency
            var transparencyToken = request["transparency"];
            int? transparency = null;
            if (transparencyToken != null && transparencyToken.Type != JTokenType.Null)
            {
                transparency = transparencyToken.Value<int>();
                if (transparency < 0 || transparency > 100)
                    return CommandResult.Fail("Transparency must be between 0 and 100.");
            }

            var materialClass = request.Value<string>("material_class");
            var materialCategory = request.Value<string>("material_category");

            using (var tx = new Transaction(doc, "RvtMcp: create material"))
            {
                tx.Start();
                try
                {
                    var matId = Material.Create(doc, name);
                    var mat = doc.GetElement(matId) as Material;
                    if (mat == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Revit failed to create the material.");
                    }

                    if (materialClass != null)
                        mat.MaterialClass = materialClass;

                    if (materialCategory != null)
                        mat.MaterialCategory = materialCategory;

                    if (hasRgb)
                        mat.Color = new Color((byte)red.Value, (byte)green.Value, (byte)blue.Value);

                    if (transparency.HasValue)
                        mat.Transparency = transparency.Value;

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {status}.");

                    return CommandResult.Ok(new
                    {
                        created = true,
                        material_id = RevitCompat.GetId(mat.Id),
                        name = mat.Name,
                        material_class = mat.MaterialClass ?? "",
                        material_category = mat.MaterialCategory ?? "",
                        color = hasRgb ? new { red = red.Value, green = green.Value, blue = blue.Value } : null,
                        transparency = mat.Transparency,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create material: " + ex.Message);
                }
            }
        }
    }
}
