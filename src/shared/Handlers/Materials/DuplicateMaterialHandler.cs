using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class DuplicateMaterialHandler : IRevitCommand
    {
        public string Name => "duplicate_material";
        public string Description => "Duplicate an existing material with a new name";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""new_name""],
  ""properties"": {
    ""source_material_id"": { ""type"": ""integer"" },
    ""source_material_name"": { ""type"": ""string"" },
    ""new_name"": { ""type"": ""string"" }
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

            var newName = request.Value<string>("new_name");
            if (string.IsNullOrWhiteSpace(newName))
                return CommandResult.Fail("new_name is required.");

            // Preflight duplicate new_name
            var allMats = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .ToList();

            if (allMats.Any(m => m.Name.Equals(newName, StringComparison.Ordinal)))
                return CommandResult.Fail($"A material named '{newName}' already exists.");

            var sourceMaterialId = request.Value<long?>("source_material_id");
            var sourceMaterialName = request.Value<string>("source_material_name") ?? "";

            Material sourceMat = null;

            if (sourceMaterialId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(sourceMaterialId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(sourceMaterialId.Value));

                var elId = RevitCompat.ToElementId(sourceMaterialId.Value);
                sourceMat = doc.GetElement(elId) as Material;
                if (sourceMat == null)
                    return CommandResult.Fail($"Source material with ID {sourceMaterialId} not found.");
            }
            else if (!string.IsNullOrEmpty(sourceMaterialName))
            {
                var matching = allMats
                    .Where(m => m.Name.Equals(sourceMaterialName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matching.Count == 0)
                    return CommandResult.Fail($"Source material with name '{sourceMaterialName}' not found.");
                if (matching.Count > 1)
                    return CommandResult.Fail($"Multiple materials found with name '{sourceMaterialName}'. Please use source_material_id.");

                sourceMat = matching[0];
            }
            else
            {
                return CommandResult.Fail("Either source_material_id or source_material_name must be supplied.");
            }

            using (var tx = new Transaction(doc, "RvtMcp: duplicate material"))
            {
                tx.Start();
                try
                {
                    var duplicatedMat = sourceMat.Duplicate(newName);
                    if (duplicatedMat == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Revit failed to duplicate the material.");
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {status}.");

                    return CommandResult.Ok(new
                    {
                        duplicated = true,
                        source_material_id = RevitCompat.GetId(sourceMat.Id),
                        new_material_id = RevitCompat.GetId(duplicatedMat.Id),
                        new_name = duplicatedMat.Name,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to duplicate material: " + ex.Message);
                }
            }
        }
    }
}
