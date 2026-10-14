using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetTypeParameterValuesHandler : IRevitCommand
    {
        public string Name => "set_type_parameter_values";
        public string Description => "Set a type parameter value for ElementType records resolved from explicit type IDs and/or source element IDs.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""parameterName"":{""type"":""string""},""value"":{""type"":""string""},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""valueType"":{""type"":""string"",""enum"":[""auto"",""string"",""integer"",""double"",""elementId""],""default"":""auto""}},""required"":[""parameterName"",""value""]}";

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

            var parameterName = request.Value<string>("parameterName");
            if (string.IsNullOrWhiteSpace(parameterName))
                return CommandResult.Fail("parameterName is required.");

            var valueToken = request["value"];
            if (valueToken == null || valueToken.Type != JTokenType.String)
                return CommandResult.Fail("value is required and must be a string.");

            var value = valueToken.Value<string>();
            var requestedValueType = request.Value<string>("valueType") ?? "auto";
            requestedValueType = CanonicalizeValueType(requestedValueType.Trim());
            if (!IsSupportedValueType(requestedValueType))
                return CommandResult.Fail("valueType must be one of: auto, string, integer, double, elementId.");

            if (!TryReadIdArray(request, "elementIds", out var elementIds, out var elementIdsError))
                return CommandResult.Fail(elementIdsError);

            if (!TryReadIdArray(request, "typeIds", out var explicitTypeIds, out var typeIdsError))
                return CommandResult.Fail(typeIdsError);

            if (elementIds.Count == 0 && explicitTypeIds.Count == 0)
                return CommandResult.Fail("Provide at least one ID in elementIds or typeIds.");

            var orderedTypeIds = new List<long>();
            var sourceElementIdsByType = new Dictionary<long, List<long>>();
            var updated = new JArray();
            var failed = new JArray();
            var errors = new JArray();

            ResolveElementTypeIds(doc, elementIds, orderedTypeIds, sourceElementIdsByType, failed, errors);

            foreach (var typeId in explicitTypeIds)
                AddTypeId(orderedTypeIds, typeId);

            using (var tx = new Transaction(doc, "MCP: Set type parameter values"))
            {
                try
                {
                    tx.Start();

                    foreach (var typeId in orderedTypeIds)
                    {
                        SetTypeParameter(
                            doc,
                            typeId,
                            GetSourceElementIds(sourceElementIdsByType, typeId),
                            parameterName,
                            value,
                            requestedValueType,
                            updated,
                            failed,
                            errors);
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit type parameter updates. Transaction status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted())
                        tx.RollBack();

                    return CommandResult.Fail("Failed to set type parameter values: " + ex.Message);
                }
            }

            return CommandResult.Ok(new
            {
                requestedTypes = orderedTypeIds.Count,
                requestedElementIds = elementIds.Count,
                requestedExplicitTypeIds = explicitTypeIds.Count,
                parameterName,
                value,
                valueType = requestedValueType,
                updatedCount = updated.Count,
                failedCount = failed.Count,
                updated,
                failed,
                errors
            });
        }

        private static void ResolveElementTypeIds(
            Document doc,
            List<long> elementIds,
            List<long> orderedTypeIds,
            Dictionary<long, List<long>> sourceElementIdsByType,
            JArray failed,
            JArray errors)
        {
            foreach (var elementId in elementIds)
            {
                var result = new JObject
                {
                    ["source"] = "elementIds",
                    ["elementId"] = elementId
                };

                try
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(elementId));
                    if (element == null)
                    {
                        AddFailure(result, failed, errors, "Element not found.");
                        continue;
                    }

                    result["name"] = element.Name;
                    result["category"] = element.Category?.Name;

                    var typeId = element.GetTypeId();
                    if (!IsValidElementId(typeId))
                    {
                        AddFailure(result, failed, errors, "Element has no valid type ID.");
                        continue;
                    }

                    var typeIdValue = RevitCompat.GetId(typeId);
                    AddTypeId(orderedTypeIds, typeIdValue);
                    AddSourceElementId(sourceElementIdsByType, typeIdValue, elementId);
                }
                catch (Exception ex)
                {
                    AddFailure(result, failed, errors, ex.Message);
                }
            }
        }

        private static void SetTypeParameter(
            Document doc,
            long typeId,
            long[] sourceElementIds,
            string parameterName,
            string value,
            string requestedValueType,
            JArray updated,
            JArray failed,
            JArray errors)
        {
            var result = new JObject
            {
                ["typeId"] = typeId,
                ["sourceElementIds"] = ToJArray(sourceElementIds)
            };

            try
            {
                var typeElement = doc.GetElement(RevitCompat.ToElementId(typeId)) as ElementType;
                if (typeElement == null)
                {
                    AddFailure(result, failed, errors, "Element type not found.");
                    return;
                }

                result["typeName"] = typeElement.Name;
                result["familyName"] = (typeElement as FamilySymbol)?.FamilyName;
                result["category"] = typeElement.Category?.Name;

                var parameter = FindParameter(typeElement, parameterName);
                if (parameter == null)
                {
                    AddFailure(result, failed, errors, "Parameter not found.");
                    return;
                }

                result["storageType"] = parameter.StorageType.ToString();
                result["isReadOnly"] = SafeIsReadOnly(parameter);
                result["oldValue"] = GetRawValue(parameter);
                result["oldDisplayValue"] = GetDisplayValue(doc, parameter);

                if (SafeIsReadOnly(parameter))
                {
                    result["newValue"] = result["oldValue"]?.DeepClone();
                    result["newDisplayValue"] = result["oldDisplayValue"]?.DeepClone();
                    AddFailure(result, failed, errors, "Parameter is read-only.");
                    return;
                }

                if (!TrySetParameterValue(parameter, value, requestedValueType, result, out var error))
                {
                    result["newValue"] = GetRawValue(parameter);
                    result["newDisplayValue"] = GetDisplayValue(doc, parameter);
                    AddFailure(result, failed, errors, error);
                    return;
                }

                result["newValue"] = GetRawValue(parameter);
                result["newDisplayValue"] = GetDisplayValue(doc, parameter);
                updated.Add(result);
            }
            catch (Exception ex)
            {
                if (result["oldDisplayValue"] != null && result["newDisplayValue"] == null)
                    result["newDisplayValue"] = result["oldDisplayValue"]?.DeepClone();

                AddFailure(result, failed, errors, ex.Message);
            }
        }

        private static bool TrySetParameterValue(
            Parameter parameter,
            string value,
            string requestedValueType,
            JObject result,
            out string error)
        {
            error = null;

            var effectiveValueType = ResolveValueType(parameter.StorageType, requestedValueType, out error);
            if (error != null)
                return false;

            result["valueTypeUsed"] = effectiveValueType;

            switch (effectiveValueType)
            {
                case "string":
                    return SetString(parameter, value, out error);

                case "integer":
                    return SetInteger(parameter, value, out error);

                case "double":
                    return SetDouble(parameter, value, result, out error);

                case "elementId":
                    return SetElementId(parameter, value, out error);

                default:
                    error = "Unsupported parameter storage type: " + parameter.StorageType;
                    return false;
            }
        }

        private static bool SetString(Parameter parameter, string value, out string error)
        {
            error = null;

            if (parameter.StorageType != StorageType.String)
            {
                error = "valueType string is incompatible with parameter storage type " + parameter.StorageType + ".";
                return false;
            }

            return SetAndCheck(parameter.Set(value), out error);
        }

        private static bool SetInteger(Parameter parameter, string value, out string error)
        {
            error = null;

            if (parameter.StorageType != StorageType.Integer)
            {
                error = "valueType integer is incompatible with parameter storage type " + parameter.StorageType + ".";
                return false;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                error = "Value must be an integer.";
                return false;
            }

            return SetAndCheck(parameter.Set(parsed), out error);
        }

        private static bool SetDouble(Parameter parameter, string value, JObject result, out string error)
        {
            error = null;

            if (parameter.StorageType != StorageType.Double)
            {
                error = "valueType double is incompatible with parameter storage type " + parameter.StorageType + ".";
                return false;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                error = "Value must be a number.";
                return false;
            }

            var internalValue = ConvertDoubleToInternal(parameter, parsed, out var interpretedUnit);
            result["inputDouble"] = parsed;
            result["inputUnit"] = interpretedUnit;
            result["internalDouble"] = internalValue;

            return SetAndCheck(parameter.Set(internalValue), out error);
        }

        private static bool SetElementId(Parameter parameter, string value, out string error)
        {
            error = null;

            if (parameter.StorageType != StorageType.ElementId)
            {
                error = "valueType elementId is incompatible with parameter storage type " + parameter.StorageType + ".";
                return false;
            }

            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                error = "Value must be an element id integer.";
                return false;
            }

#if !REVIT2024_OR_GREATER
            if (parsed < int.MinValue || parsed > int.MaxValue)
            {
                error = "Element id is outside the Revit 2022 integer range.";
                return false;
            }
#endif

            return SetAndCheck(parameter.Set(RevitCompat.ToElementId(parsed)), out error);
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

        private static string ResolveValueType(StorageType storageType, string requestedValueType, out string error)
        {
            error = null;

            var normalized = CanonicalizeValueType(requestedValueType);
            if (normalized != "auto")
                return normalized;

            switch (storageType)
            {
                case StorageType.String:
                    return "string";
                case StorageType.Integer:
                    return "integer";
                case StorageType.Double:
                    return "double";
                case StorageType.ElementId:
                    return "elementId";
                default:
                    error = "Unsupported parameter storage type: " + storageType + ".";
                    return null;
            }
        }

        private static double ConvertDoubleToInternal(Parameter parameter, double value, out string interpretedUnit)
        {
            switch (GetDoubleSpec(parameter))
            {
                case "length":
                    interpretedUnit = "millimeters";
                    return value / 304.8;

                case "area":
                    interpretedUnit = "squareMillimeters";
                    return value / (304.8 * 304.8);

                case "volume":
                    interpretedUnit = "cubicMillimeters";
                    return value / (304.8 * 304.8 * 304.8);

                case "angle":
                    interpretedUnit = "degrees";
                    return value * Math.PI / 180.0;

                default:
                    interpretedUnit = "internal";
                    return value;
            }
        }

        private static string GetDoubleSpec(Parameter parameter)
        {
            string typeId;
            try
            {
                typeId = parameter.Definition?.GetDataType()?.TypeId;
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(typeId))
                return null;

            if (TypeIdMatchesSpec(typeId, "length"))
                return "length";
            if (TypeIdMatchesSpec(typeId, "area"))
                return "area";
            if (TypeIdMatchesSpec(typeId, "volume"))
                return "volume";
            if (TypeIdMatchesSpec(typeId, "angle"))
                return "angle";

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

        private static Parameter FindParameter(ElementType typeElement, string parameterName)
        {
            var parameter = typeElement.LookupParameter(parameterName);
            if (parameter != null)
                return parameter;

            foreach (Parameter candidate in typeElement.Parameters)
            {
                if (candidate?.Definition?.Name != null &&
                    candidate.Definition.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
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
                        return ToJToken(RevitCompat.GetIdOrNull(parameter.AsElementId()));

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
            catch
            {
            }

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
                        if (id == null)
                            return null;

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

        private static bool TryReadIdArray(JObject request, string propertyName, out List<long> ids, out string error)
        {
            ids = new List<long>();
            error = null;

            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Array)
            {
                error = propertyName + " must be an array of integers.";
                return false;
            }

            var array = (JArray)token;
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i].Type != JTokenType.Integer)
                {
                    error = propertyName + "[" + i.ToString(CultureInfo.InvariantCulture) + "] must be an integer.";
                    return false;
                }

                var id = array[i].Value<long>();
                if (!RevitCompat.CanRepresentElementId(id))
                {
                    error = propertyName + "[" + i.ToString(CultureInfo.InvariantCulture) + "] " + RevitCompat.ElementIdRangeError(id);
                    return false;
                }

                ids.Add(id);
            }

            return true;
        }

        private static void AddTypeId(List<long> orderedTypeIds, long typeId)
        {
            if (!orderedTypeIds.Contains(typeId))
                orderedTypeIds.Add(typeId);
        }

        private static void AddSourceElementId(Dictionary<long, List<long>> sourceElementIdsByType, long typeId, long elementId)
        {
            if (!sourceElementIdsByType.TryGetValue(typeId, out var sourceElementIds))
            {
                sourceElementIds = new List<long>();
                sourceElementIdsByType[typeId] = sourceElementIds;
            }

            if (!sourceElementIds.Contains(elementId))
                sourceElementIds.Add(elementId);
        }

        private static long[] GetSourceElementIds(Dictionary<long, List<long>> sourceElementIdsByType, long typeId)
        {
            return sourceElementIdsByType.TryGetValue(typeId, out var sourceElementIds)
                ? sourceElementIds.ToArray()
                : new long[0];
        }

        private static bool IsSupportedValueType(string valueType)
        {
            return string.Equals(valueType, "auto", StringComparison.Ordinal)
                || string.Equals(valueType, "string", StringComparison.Ordinal)
                || string.Equals(valueType, "integer", StringComparison.Ordinal)
                || string.Equals(valueType, "double", StringComparison.Ordinal)
                || string.Equals(valueType, "elementId", StringComparison.Ordinal);
        }

        private static string CanonicalizeValueType(string valueType)
        {
            if (string.Equals(valueType, "elementId", StringComparison.OrdinalIgnoreCase))
                return "elementId";

            return (valueType ?? "auto").ToLowerInvariant();
        }

        private static bool SafeIsReadOnly(Parameter parameter)
        {
            try
            {
                return parameter?.IsReadOnly ?? false;
            }
            catch
            {
                return false;
            }
        }

        private static void AddFailure(JObject result, JArray failed, JArray errors, string reason)
        {
            result["error"] = reason;
            failed.Add(result);

            var label = result["typeId"] != null
                ? "typeId " + result["typeId"].ToString(Formatting.None)
                : "elementId " + (result["elementId"]?.ToString(Formatting.None) ?? "(unknown)");
            errors.Add(label + ": " + reason);
        }

        private static JArray ToJArray(IEnumerable<long> ids)
        {
            var array = new JArray();
            foreach (var id in ids)
                array.Add(id);
            return array;
        }

        private static JToken ToJToken(long? value)
        {
            return value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
        }

        private static bool IsValidElementId(ElementId id)
        {
            return id != null && RevitCompat.GetId(id) != RevitCompat.GetId(ElementId.InvalidElementId);
        }
    }
}
