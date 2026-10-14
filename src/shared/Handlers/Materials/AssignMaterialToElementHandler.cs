using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AssignMaterialToElementHandler : IRevitCommand
    {
        public string Name => "assign_material_to_element";
        public string Description => "Assign a material to specific element parameters or compound layer indices";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""material_id"": { ""type"": ""integer"" },
    ""material_name"": { ""type"": ""string"" },
    ""parameter_name"": { ""type"": ""string"" },
    ""compound_layer_index"": { ""type"": ""integer"", ""minimum"": 0 },
    ""allow_type_mutation"": { ""type"": ""boolean"", ""default"": false },
    ""duplicate_type_name"": { ""type"": ""string"" }
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

            var elementIds = request["element_ids"]?.ToObject<long[]>() ?? new long[0];
            if (elementIds.Length == 0)
                return CommandResult.Fail("element_ids array is required.");

            var materialId = request.Value<long?>("material_id");
            var materialName = request.Value<string>("material_name") ?? "";
            var parameterName = request.Value<string>("parameter_name") ?? "";
            var compoundLayerIndex = request.Value<int?>("compound_layer_index");
            var allowTypeMutation = request.Value<bool?>("allow_type_mutation") ?? false;
            var duplicateTypeName = request.Value<string>("duplicate_type_name") ?? "";

            // 1. Resolve elements
            var elements = new List<Element>();
            foreach (var rawId in elementIds)
            {
                if (!RevitCompat.CanRepresentElementId(rawId))
                    return CommandResult.Fail($"Element ID {rawId} " + RevitCompat.ElementIdRangeError(rawId));

                var el = doc.GetElement(RevitCompat.ToElementId(rawId));
                if (el == null)
                    return CommandResult.Fail($"Element with ID {rawId} not found.");
                elements.Add(el);
            }

            // 2. Resolve material
            Material material = null;
            if (materialId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(materialId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(materialId.Value));

                var elId = RevitCompat.ToElementId(materialId.Value);
                material = doc.GetElement(elId) as Material;
                if (material == null)
                    return CommandResult.Fail($"Material with ID {materialId.Value} not found.");
            }
            else if (!string.IsNullOrEmpty(materialName))
            {
                var matching = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .Where(m => m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matching.Count == 0)
                    return CommandResult.Fail($"Material with name '{materialName}' not found.");
                if (matching.Count > 1)
                    return CommandResult.Fail($"Multiple materials found with name '{materialName}'. Please use material_id.");

                material = matching[0];
            }
            else
            {
                return CommandResult.Fail("Either material_id or material_name is required.");
            }

            // 3. Preflight and Validate Strategy for all elements
            foreach (var el in elements)
            {
                if (compoundLayerIndex.HasValue)
                {
                    var typeId = el.GetTypeId();
                    if (typeId == null || typeId == ElementId.InvalidElementId)
                        return CommandResult.Fail($"Element {RevitCompat.GetId(el.Id)} has no valid type.");

                    var hostType = doc.GetElement(typeId) as HostObjAttributes;
                    if (hostType == null)
                        return CommandResult.Fail($"Element {RevitCompat.GetId(el.Id)} type does not support compound structures.");

                    var cs = hostType.GetCompoundStructure();
                    if (cs == null)
                        return CommandResult.Fail($"Element {RevitCompat.GetId(el.Id)} type compound structure is null.");

                    if (compoundLayerIndex.Value < 0 || compoundLayerIndex.Value >= cs.LayerCount)
                        return CommandResult.Fail($"Compound layer index {compoundLayerIndex.Value} is out of range for element {RevitCompat.GetId(el.Id)} type (valid range: 0 to {cs.LayerCount - 1}).");

                    if (!allowTypeMutation)
                    {
                        if (string.IsNullOrWhiteSpace(duplicateTypeName))
                            return CommandResult.Fail("duplicate_type_name is required when allow_type_mutation is false.");

                        // Check name collision
                        var coll = new FilteredElementCollector(doc)
                            .OfClass(hostType.GetType())
                            .Cast<ElementType>()
                            .Any(t => t.Name.Equals(duplicateTypeName, StringComparison.OrdinalIgnoreCase));
                        if (coll)
                            return CommandResult.Fail($"A family type named '{duplicateTypeName}' already exists.");
                    }
                }
                else
                {
                    var param = FindMaterialParameter(el, parameterName);
                    if (param == null)
                    {
                        return CommandResult.Fail($"Could not find a writable material parameter on element {RevitCompat.GetId(el.Id)}.");
                    }
                }
            }

            if (compoundLayerIndex.HasValue && !allowTypeMutation)
            {
                var distinctSourceTypeIds = elements
                    .Select(el => RevitCompat.GetId(el.GetTypeId()))
                    .Distinct()
                    .ToList();

                if (distinctSourceTypeIds.Count > 1)
                    return CommandResult.Fail("duplicate_type_name can only be used with elements that share one source host type. Split multi-type requests or allow type mutation.");
            }

            // 4. Perform atomic assignment inside transaction
            var results = new List<object>();
            var duplicatedTypes = new Dictionary<long, HostObjAttributes>();

            using (var tx = new Transaction(doc, "RvtMcp: assign material"))
            {
                tx.Start();
                try
                {
                    foreach (var el in elements)
                    {
                        long elIdVal = RevitCompat.GetId(el.Id);

                        if (compoundLayerIndex.HasValue)
                        {
                            var typeId = el.GetTypeId();
                            var hostType = doc.GetElement(typeId) as HostObjAttributes;

                            if (!allowTypeMutation)
                            {
                                var sourceTypeId = RevitCompat.GetId(hostType.Id);
                                if (!duplicatedTypes.TryGetValue(sourceTypeId, out var newType))
                                {
                                    newType = hostType.Duplicate(duplicateTypeName) as HostObjAttributes;
                                    if (newType == null)
                                        throw new InvalidOperationException("Revit failed to duplicate the host type.");

                                    var cs = newType.GetCompoundStructure();
                                    cs.SetMaterialId((int)compoundLayerIndex.Value, material.Id);
                                    newType.SetCompoundStructure(cs);
                                    duplicatedTypes[sourceTypeId] = newType;
                                }

                                el.ChangeTypeId(newType.Id);

                                results.Add(new
                                {
                                    element_id = elIdVal,
                                    status = "set",
                                    mode = "compound_layer",
                                    compound_layer_index = compoundLayerIndex.Value,
                                    mutated_type_id = RevitCompat.GetId(newType.Id),
                                    mutated_type_name = duplicateTypeName
                                });
                            }
                            else
                            {
                                var cs = hostType.GetCompoundStructure();
                                cs.SetMaterialId((int)compoundLayerIndex.Value, material.Id);
                                hostType.SetCompoundStructure(cs);

                                results.Add(new
                                {
                                    element_id = elIdVal,
                                    status = "set",
                                    mode = "compound_layer",
                                    compound_layer_index = compoundLayerIndex.Value,
                                    mutated_type_id = RevitCompat.GetId(hostType.Id),
                                    mutated_type_name = hostType.Name
                                });
                            }
                        }
                        else
                        {
                            var param = FindMaterialParameter(el, parameterName);
                            param.Set(material.Id);

                            results.Add(new
                            {
                                element_id = elIdVal,
                                status = "set",
                                mode = "parameter",
                                parameter_name = param.Definition?.Name ?? ""
                            });
                        }
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {status}.");

                    return CommandResult.Ok(new
                    {
                        updated = true,
                        material_id = RevitCompat.GetId(material.Id),
                        results,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to assign material: " + ex.Message);
                }
            }
        }

        private static Parameter FindMaterialParameter(Element element, string parameterName)
        {
            if (!string.IsNullOrEmpty(parameterName))
            {
                var p = element.LookupParameter(parameterName);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.ElementId)
                    return p;

                foreach (Parameter param in element.Parameters)
                {
                    if (param != null && !param.IsReadOnly && param.StorageType == StorageType.ElementId &&
                        param.Definition != null && param.Definition.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                    {
                        return param;
                    }
                }
            }
            else
            {
                var structuralMatParam = element.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                if (structuralMatParam != null && !structuralMatParam.IsReadOnly)
                    return structuralMatParam;

                foreach (Parameter param in element.Parameters)
                {
                    if (param != null && !param.IsReadOnly && param.StorageType == StorageType.ElementId)
                    {
                        var name = param.Definition?.Name ?? "";
                        if (name.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return param;
                        }
                    }
                }
            }
            return null;
        }
    }
}
