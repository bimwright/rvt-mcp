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
    public class SetParameterValueByGuidHandler : IRevitCommand
    {
        public string Name => "set_parameter_value_by_guid";
        public string Description => "Set shared parameter value by its GUID for one or more elements.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""elementIds"", ""guid"", ""value""],
  ""properties"": {
    ""elementIds"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""guid"": { ""type"": ""string"" },
    ""value"": {
      ""type"": ""string"",
      ""description"": ""String transport value parsed according to parameter storage type.""
    },
    ""valueType"": {
      ""type"": ""string"",
      ""enum"": [""auto"", ""string"", ""integer"", ""double"", ""elementId"", ""display""],
      ""default"": ""auto""
    },
    ""unit"": {
      ""type"": ""string"",
      ""enum"": [""auto"", ""internal"", ""mm"", ""m2"", ""m3"", ""deg""],
      ""default"": ""auto""
    },
    ""target"": {
      ""type"": ""string"",
      ""enum"": [""auto"", ""instance"", ""type""],
      ""default"": ""auto""
    },
    ""allOrNothing"": { ""type"": ""boolean"", ""default"": true }
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

            var elementIdsInput = request["elementIds"]?.ToObject<long[]>();
            var guidInput = request.Value<string>("guid");
            var valueToken = request["value"];
            var valueType = request.Value<string>("valueType") ?? "auto";
            var unit = request.Value<string>("unit") ?? "auto";
            var target = request.Value<string>("target") ?? "auto";
            var allOrNothing = request.Value<bool?>("allOrNothing") ?? true;

            if (elementIdsInput == null || elementIdsInput.Length == 0)
                return CommandResult.Fail("elementIds is required and cannot be empty.");

            if (string.IsNullOrWhiteSpace(guidInput) || !Guid.TryParse(guidInput, out var guidObj))
                return CommandResult.Fail("A valid guid is required.");

            if (valueToken == null || valueToken.Type != JTokenType.String)
                return CommandResult.Fail("value is required and must be a string.");

            var valueStr = valueToken.Value<string>();

            // Pre-validate all element IDs can be represented
            foreach (var idVal in elementIdsInput)
            {
                if (!RevitCompat.CanRepresentElementId(idVal))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(idVal));
            }

            var updated = new List<object>();
            var failed = new List<object>();
            var skippedDuplicateTypeIds = new List<long>();
            var processedTypeIds = new HashSet<long>();
            var warnings = new List<string>();

            // 1. Resolve target parameter for each element, check existence & write-ability
            var targets = new List<ParameterTargetInfo>();
            for (int i = 0; i < elementIdsInput.Length; i++)
            {
                var elId = elementIdsInput[i];
                var element = doc.GetElement(RevitCompat.ToElementId(elId));
                if (element == null)
                {
                    failed.Add(new { elementId = elId, error = "Element not found." });
                    continue;
                }

                Parameter parameter = null;
                string targetUsed = "instance";
                Element resolvedTargetElement = element;

                if (string.Equals(target, "instance", StringComparison.OrdinalIgnoreCase))
                {
                    parameter = element.get_Parameter(guidObj);
                }
                else if (string.Equals(target, "type", StringComparison.OrdinalIgnoreCase))
                {
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        var typeElement = doc.GetElement(typeId);
                        if (typeElement != null)
                        {
                            parameter = typeElement.get_Parameter(guidObj);
                            targetUsed = "type";
                            resolvedTargetElement = typeElement;
                        }
                    }
                }
                else // auto
                {
                    parameter = element.get_Parameter(guidObj);
                    if (parameter == null)
                    {
                        var typeId = element.GetTypeId();
                        if (typeId != ElementId.InvalidElementId)
                        {
                            var typeElement = doc.GetElement(typeId);
                            if (typeElement != null)
                            {
                                parameter = typeElement.get_Parameter(guidObj);
                                if (parameter != null)
                                {
                                    targetUsed = "type";
                                    resolvedTargetElement = typeElement;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Check if type parameter also exists to warn
                        var typeId = element.GetTypeId();
                        if (typeId != ElementId.InvalidElementId)
                        {
                            var typeElement = doc.GetElement(typeId);
                            if (typeElement != null && typeElement.get_Parameter(guidObj) != null)
                            {
                                warnings.Add($"Element {elId} has both instance and type parameter for GUID {guidInput}. Preferring instance.");
                            }
                        }
                    }
                }

                if (parameter == null)
                {
                    failed.Add(new { elementId = elId, error = $"Parameter GUID {guidInput} not found on this element or its type." });
                    continue;
                }

                if (parameter.IsReadOnly)
                {
                    failed.Add(new { elementId = elId, error = "Parameter is read-only." });
                    continue;
                }

                if (targetUsed == "type")
                {
                    long typeIdVal = RevitCompat.GetId(resolvedTargetElement.Id);
                    if (processedTypeIds.Contains(typeIdVal))
                    {
                        skippedDuplicateTypeIds.Add(typeIdVal);
                        continue;
                    }
                    processedTypeIds.Add(typeIdVal);
                }

                targets.Add(new ParameterTargetInfo
                {
                    ElementId = elId,
                    ElementName = element.Name,
                    CategoryName = element.Category?.Name,
                    Parameter = parameter,
                    TargetUsed = targetUsed,
                    TargetElement = resolvedTargetElement
                });
            }

            // If allOrNothing=true and we have any pre-validation failures, return Fail
            if (allOrNothing && failed.Count > 0)
            {
                var firstErr = failed[0] as dynamic;
                return CommandResult.Fail($"Validation failed for element {firstErr.elementId}: {firstErr.error}");
            }

            // 2. Perform transaction / mutations
            if (allOrNothing)
            {
                using (var tx = new Transaction(doc, "RvtMcp: set parameter by GUID"))
                {
                    tx.Start();

                    foreach (var t in targets)
                    {
                        var oldVal = GetRawValue(t.Parameter);
                        var oldDisplay = GetDisplayValue(doc, t.Parameter);

                        if (!TrySetParameterValue(t.Parameter, valueStr, valueType, unit, out var unitUsed, out var typeUsed, out var err))
                        {
                            tx.RollBack();
                            return CommandResult.Fail($"Failed to set parameter value for element {t.ElementId}: {err}");
                        }

                        var newVal = GetRawValue(t.Parameter);
                        var newDisplay = GetDisplayValue(doc, t.Parameter);

                        updated.Add(new
                        {
                            elementId = t.ElementId,
                            name = t.ElementName,
                            category = t.CategoryName,
                            parameterName = t.Parameter.Definition.Name,
                            storageType = t.Parameter.StorageType.ToString(),
                            oldValue = oldVal,
                            oldDisplayValue = oldDisplay,
                            newValue = newVal,
                            newDisplayValue = newDisplay,
                            valueTypeUsed = typeUsed,
                            unitUsed = unitUsed,
                            targetUsed = t.TargetUsed
                        });
                    }

                    var commitStatus = tx.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail($"Transaction commit failed: {commitStatus}");
                    }
                }
            }
            else
            {
                using (var tx = new Transaction(doc, "RvtMcp: set parameter by GUID"))
                {
                    tx.Start();

                    foreach (var t in targets)
                    {
                        using (var subTx = new SubTransaction(doc))
                        {
                            subTx.Start();

                            var oldVal = GetRawValue(t.Parameter);
                            var oldDisplay = GetDisplayValue(doc, t.Parameter);

                            if (!TrySetParameterValue(t.Parameter, valueStr, valueType, unit, out var unitUsed, out var typeUsed, out var err))
                            {
                                subTx.RollBack();
                                failed.Add(new { elementId = t.ElementId, error = err });
                            }
                            else
                            {
                                var subStatus = subTx.Commit();
                                if (subStatus != TransactionStatus.Committed)
                                {
                                    failed.Add(new { elementId = t.ElementId, error = "Subtransaction did not commit: " + subStatus });
                                    continue;
                                }
                                var newVal = GetRawValue(t.Parameter);
                                var newDisplay = GetDisplayValue(doc, t.Parameter);

                                updated.Add(new
                               {
                                   elementId = t.ElementId,
                                   name = t.ElementName,
                                   category = t.CategoryName,
                                   parameterName = t.Parameter.Definition.Name,
                                   storageType = t.Parameter.StorageType.ToString(),
                                   oldValue = oldVal,
                                   oldDisplayValue = oldDisplay,
                                   newValue = newVal,
                                   newDisplayValue = newDisplay,
                                   valueTypeUsed = typeUsed,
                                   unitUsed = unitUsed,
                                   targetUsed = t.TargetUsed
                               });
                            }
                        }
                    }

                    var commitStatus = tx.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail($"Transaction commit failed: {commitStatus}");
                    }
                }
            }

            return CommandResult.Ok(new
            {
                requested = elementIdsInput.Length,
                updatedCount = updated.Count,
                failedCount = failed.Count,
                guid = guidObj.ToString("d"),
                value = valueStr,
                valueType,
                unit,
                target,
                allOrNothing,
                updated,
                failed,
                skippedDuplicateTypeIds = skippedDuplicateTypeIds.ToArray(),
                warnings = warnings.ToArray()
            });
        }

        private static bool TrySetParameterValue(
            Parameter parameter,
            string value,
            string requestedValueType,
            string requestedUnit,
            out string unitUsed,
            out string typeUsed,
            out string error)
        {
            error = null;
            unitUsed = null;
            typeUsed = requestedValueType;

            if (string.Equals(requestedValueType, "display", StringComparison.OrdinalIgnoreCase))
            {
                typeUsed = "display";
                if (parameter.SetValueString(value))
                {
                    return true;
                }
                error = "Revit rejected the parameter display value.";
                return false;
            }

            var effectiveValueType = ResolveValueType(parameter.StorageType, requestedValueType, out error);
            if (error != null)
                return false;

            typeUsed = effectiveValueType;

            switch (effectiveValueType)
            {
                case "string":
                    if (parameter.StorageType != StorageType.String)
                    {
                        error = $"valueType string is incompatible with storage type {parameter.StorageType}.";
                        return false;
                    }
                    return SetAndCheck(parameter.Set(value), out error);

                case "integer":
                    if (parameter.StorageType != StorageType.Integer)
                    {
                        error = $"valueType integer is incompatible with storage type {parameter.StorageType}.";
                        return false;
                    }
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                    {
                        error = "Value must be an integer.";
                        return false;
                    }
                    return SetAndCheck(parameter.Set(parsedInt), out error);

                case "double":
                    if (parameter.StorageType != StorageType.Double)
                    {
                        error = $"valueType double is incompatible with storage type {parameter.StorageType}.";
                        return false;
                    }
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
                    {
                        error = "Value must be a number.";
                        return false;
                    }

                    var internalDouble = ConvertDoubleToInternal(parameter, parsedDouble, requestedUnit, out unitUsed, out error);
                    if (error != null)
                        return false;

                    return SetAndCheck(parameter.Set(internalDouble), out error);

                case "elementId":
                    if (parameter.StorageType != StorageType.ElementId)
                    {
                        error = $"valueType elementId is incompatible with storage type {parameter.StorageType}.";
                        return false;
                    }
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedElId))
                    {
                        error = "Value must be an element id integer.";
                        return false;
                    }
                    if (!RevitCompat.CanRepresentElementId(parsedElId))
                    {
                        error = "Element ID is outside the Revit integer range.";
                        return false;
                    }
                    return SetAndCheck(parameter.Set(RevitCompat.ToElementId(parsedElId)), out error);

                default:
                    error = "Unsupported parameter storage type: " + parameter.StorageType;
                    return false;
            }
        }

        private static string ResolveValueType(StorageType storageType, string requestedValueType, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(requestedValueType) || string.Equals(requestedValueType, "auto", StringComparison.OrdinalIgnoreCase))
            {
                switch (storageType)
                {
                    case StorageType.String: return "string";
                    case StorageType.Integer: return "integer";
                    case StorageType.Double: return "double";
                    case StorageType.ElementId: return "elementId";
                    default:
                        error = "Unsupported parameter storage type: " + storageType;
                        return null;
                }
            }
            return requestedValueType.ToLowerInvariant();
        }

        private static double ConvertDoubleToInternal(
            Parameter parameter,
            double value,
            string requestedUnit,
            out string unitUsed,
            out string error)
        {
            error = null;
            unitUsed = requestedUnit;

            string inferredSpec = GetDoubleSpec(parameter);

            if (string.Equals(requestedUnit, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(requestedUnit))
            {
                if (inferredSpec == "length")
                {
                    unitUsed = "mm";
                    return value / 304.8;
                }
                if (inferredSpec == "area")
                {
                    unitUsed = "m2";
                    return value / (304.8 * 304.8);
                }
                if (inferredSpec == "volume")
                {
                    unitUsed = "m3";
                    return value / (304.8 * 304.8 * 304.8);
                }
                if (inferredSpec == "angle")
                {
                    unitUsed = "deg";
                    return value * Math.PI / 180.0;
                }

                error = "Cannot auto-infer units for this parameter type. Please specify 'unit' explicitly (internal, mm, m2, m3, deg).";
                return 0;
            }

            switch (requestedUnit.ToLowerInvariant())
            {
                case "internal":
                    return value;
                case "mm":
                    return value / 304.8;
                case "m2":
                    return value / (304.8 * 304.8);
                case "m3":
                    return value / (304.8 * 304.8 * 304.8);
                case "deg":
                    return value * Math.PI / 180.0;
                default:
                    error = $"Unsupported unit override: {requestedUnit}";
                    return 0;
            }
        }

        private static string GetDoubleSpec(Parameter parameter)
        {
            string typeId;
            try { typeId = parameter.Definition?.GetDataType()?.TypeId; }
            catch { return null; }

            if (string.IsNullOrWhiteSpace(typeId))
                return null;

            if (TypeIdMatchesSpec(typeId, "length")) return "length";
            if (TypeIdMatchesSpec(typeId, "area")) return "area";
            if (TypeIdMatchesSpec(typeId, "volume")) return "volume";
            if (TypeIdMatchesSpec(typeId, "angle")) return "angle";

            return null;
        }

        private static bool TypeIdMatchesSpec(string typeId, string specName)
        {
            var lower = typeId.ToLowerInvariant();
            var colonPattern = ":" + specName;
            var dotPattern = "." + specName;

            return lower.EndsWith(colonPattern, StringComparison.Ordinal)
                || lower.EndsWith(dotPattern, StringComparison.Ordinal)
                || lower.Contains(colonPattern + "-")
                || lower.Contains(dotPattern + "-");
        }

        private static JToken GetRawValue(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        var stringValue = parameter.AsString();
                        return stringValue == null ? JValue.CreateNull() : new JValue(stringValue);

                    case StorageType.Integer:
                        return new JValue(parameter.AsInteger());

                    case StorageType.Double:
                        return new JValue(parameter.AsDouble());

                    case StorageType.ElementId:
                        var id = parameter.AsElementId();
                        return id == null ? JValue.CreateNull() : new JValue(RevitCompat.GetId(id));

                    default:
                        return JValue.CreateNull();
                }
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        private static string GetDisplayValue(Document doc, Parameter parameter)
        {
            try
            {
                var valueString = parameter.AsValueString();
                if (!string.IsNullOrEmpty(valueString))
                    return valueString;
            }
            catch { }

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString();

                    case StorageType.Integer:
                        return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);

                    case StorageType.Double:
                        return parameter.AsDouble().ToString("G17", CultureInfo.InvariantCulture);

                    case StorageType.ElementId:
                        var id = parameter.AsElementId();
                        if (id == null) return null;
                        var idValue = RevitCompat.GetId(id);
                        var referencedElement = doc.GetElement(id);
                        return referencedElement == null
                            ? idValue.ToString(CultureInfo.InvariantCulture)
                            : referencedElement.Name + " (" + idValue.ToString(CultureInfo.InvariantCulture) + ")";

                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool SetAndCheck(bool setResult, out string error)
        {
            if (setResult)
            {
                error = null;
                return true;
            }
            error = "Revit rejected the parameter value.";
            return false;
        }

        private class ParameterTargetInfo
        {
            public long ElementId { get; set; }
            public string ElementName { get; set; }
            public string CategoryName { get; set; }
            public Parameter Parameter { get; set; }
            public string TargetUsed { get; set; }
            public Element TargetElement { get; set; }
        }
    }
}
