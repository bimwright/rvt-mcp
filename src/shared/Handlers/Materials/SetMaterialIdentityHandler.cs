using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetMaterialIdentityHandler : IRevitCommand
    {
        public string Name => "set_material_identity";
        public string Description => "Set identity parameters for a material";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""material_id"": { ""type"": ""integer"" },
    ""material_name"": { ""type"": ""string"" },
    ""manufacturer"": { ""type"": ""string"" },
    ""model"": { ""type"": ""string"" },
    ""cost"": { ""type"": ""string"" },
    ""keynote"": { ""type"": ""string"" },
    ""mark"": { ""type"": ""string"" },
    ""url"": { ""type"": ""string"" },
    ""material_class"": { ""type"": ""string"" },
    ""material_category"": { ""type"": ""string"" }
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

            var manufacturer = request.Value<string>("manufacturer");
            var model = request.Value<string>("model");
            var cost = request.Value<string>("cost");
            var keynote = request.Value<string>("keynote");
            var mark = request.Value<string>("mark");
            var url = request.Value<string>("url");
            var materialClass = request.Value<string>("material_class");
            var materialCategory = request.Value<string>("material_category");

            int attemptedCount = 0;
            if (materialClass != null) attemptedCount++;
            if (materialCategory != null) attemptedCount++;
            if (manufacturer != null) attemptedCount++;
            if (model != null) attemptedCount++;
            if (cost != null) attemptedCount++;
            if (keynote != null) attemptedCount++;
            if (mark != null) attemptedCount++;
            if (url != null) attemptedCount++;

            if (attemptedCount == 0)
                return CommandResult.Fail("No identity fields were provided for update.");

            var fields = new Dictionary<string, object>();
            int successCount = 0;

            using (var tx = new Transaction(doc, "RvtMcp: set material identity"))
            {
                tx.Start();
                try
                {
                    if (materialClass != null)
                    {
                        mat.MaterialClass = materialClass;
                        fields["material_class"] = new { status = "set" };
                        successCount++;
                    }
                    if (materialCategory != null)
                    {
                        mat.MaterialCategory = materialCategory;
                        fields["material_category"] = new { status = "set" };
                        successCount++;
                    }

                    if (manufacturer != null)
                    {
                        if (TrySetIdentityField(mat, BuiltInParameter.ALL_MODEL_MANUFACTURER, "Manufacturer", manufacturer, out var status))
                            successCount++;
                        fields["manufacturer"] = new { status };
                    }
                    if (model != null)
                    {
                        if (TrySetIdentityField(mat, BuiltInParameter.ALL_MODEL_MODEL, "Model", model, out var status))
                            successCount++;
                        fields["model"] = new { status };
                    }
                    if (cost != null)
                    {
                        if (TrySetIdentityField(mat, BuiltInParameter.ALL_MODEL_COST, "Cost", cost, out var status))
                            successCount++;
                        fields["cost"] = new { status };
                    }
                    if (keynote != null)
                    {
                        if (TrySetIdentityField(mat, BuiltInParameter.KEYNOTE_PARAM, "Keynote", keynote, out var status))
                            successCount++;
                        fields["keynote"] = new { status };
                    }
                    if (mark != null)
                    {
                        if (TrySetIdentityField(mat, BuiltInParameter.ALL_MODEL_MARK, "Mark", mark, out var status))
                            successCount++;
                        fields["mark"] = new { status };
                    }
                    if (url != null)
                    {
                        if (TrySetIdentityField(mat, BuiltInParameter.INVALID, "URL", url, out var status))
                            successCount++;
                        fields["url"] = new { status };
                    }

                    if (successCount == 0)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("All supplied fields failed to set (they were either not found or read-only).");
                    }

                    var commitStatus = tx.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {commitStatus}.");

                    return CommandResult.Ok(new
                    {
                        updated = true,
                        material_id = RevitCompat.GetId(mat.Id),
                        name = mat.Name,
                        fields,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to set material identity: " + ex.Message);
                }
            }
        }

        private static bool TrySetIdentityField(Material mat, BuiltInParameter bip, string name, string value, out string status)
        {
            status = "ignored";
            if (value == null) return false;

            Parameter p = null;
            try
            {
                if (bip != BuiltInParameter.INVALID)
                    p = mat.get_Parameter(bip);
            }
            catch { }

            if (p == null)
            {
                try
                {
                    p = mat.LookupParameter(name);
                }
                catch { }
            }

            if (p == null)
            {
                status = "not_found";
                return false;
            }

            if (p.IsReadOnly)
            {
                status = "read_only";
                return false;
            }

            try
            {
                if (p.StorageType == StorageType.String)
                {
                    p.Set(value);
                }
                else if (p.StorageType == StorageType.Double)
                {
                    if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                        p.Set(val);
                    else
                        p.SetValueString(value);
                }
                else if (p.StorageType == StorageType.Integer)
                {
                    if (int.TryParse(value, out int val))
                        p.Set(val);
                    else
                        p.SetValueString(value);
                }
                else
                {
                    p.SetValueString(value);
                }
                status = "set";
                return true;
            }
            catch (Exception ex)
            {
                status = "error: " + ex.Message;
                return false;
            }
        }
    }
}
