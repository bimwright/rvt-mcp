using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class DuplicateFamilyTypeHandler : IRevitCommand
    {
        public string Name => "duplicate_family_type";

        public string Description => "Duplicate a FamilySymbol (type) within its family, optionally setting new type parameter values. Returns the new symbol id.";

        public string ParametersSchema => @"{""type"":""object"",""required"":[""source_type_id"",""new_type_name""],""properties"":{""source_type_id"":{""type"":""string"",""description"":""ElementId string of the source FamilySymbol.""},""new_type_name"":{""type"":""string"",""description"":""Name for the new duplicate type.""},""type_parameter_overrides"":{""type"":""object"",""description"":""Map of parameter name to value (string/number) to set on the new type after duplication.""}}}";

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

            // Read source_type_id (string per schema)
            var sourceTypeIdRaw = request["source_type_id"];
            if (sourceTypeIdRaw == null || sourceTypeIdRaw.Type == JTokenType.Null)
                return CommandResult.Fail("source_type_id is required.");

            string sourceTypeIdStr;
            if (sourceTypeIdRaw.Type == JTokenType.String)
                sourceTypeIdStr = sourceTypeIdRaw.Value<string>();
            else if (sourceTypeIdRaw.Type == JTokenType.Integer)
                sourceTypeIdStr = sourceTypeIdRaw.Value<long>().ToString(CultureInfo.InvariantCulture);
            else
                return CommandResult.Fail("source_type_id must be a string ElementId.");

            if (string.IsNullOrWhiteSpace(sourceTypeIdStr))
                return CommandResult.Fail("source_type_id is required.");

            if (!long.TryParse(sourceTypeIdStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceTypeIdValue))
                return CommandResult.Fail("source_type_id must be a numeric ElementId string.");

            if (!RevitCompat.CanRepresentElementId(sourceTypeIdValue))
                return CommandResult.Fail("source_type_id " + RevitCompat.ElementIdRangeError(sourceTypeIdValue));

            // Read new_type_name
            var newTypeNameToken = request["new_type_name"];
            if (newTypeNameToken == null || newTypeNameToken.Type != JTokenType.String)
                return CommandResult.Fail("new_type_name is required and must be a string.");

            var newTypeName = newTypeNameToken.Value<string>();
            if (string.IsNullOrWhiteSpace(newTypeName))
                return CommandResult.Fail("new_type_name must be a non-empty string.");

            // Read optional type_parameter_overrides
            JObject overrides = null;
            var overridesToken = request["type_parameter_overrides"];
            if (overridesToken != null && overridesToken.Type != JTokenType.Null)
            {
                if (overridesToken.Type != JTokenType.Object)
                    return CommandResult.Fail("type_parameter_overrides must be an object map of name -> value.");
                overrides = (JObject)overridesToken;
            }

            // Resolve source ElementType
            ElementType sourceType;
            try
            {
                sourceType = doc.GetElement(RevitCompat.ToElementId(sourceTypeIdValue)) as ElementType;
            }
            catch (Exception ex)
            {
                return BuildErrorDto("Failed to resolve source_type_id: " + ex.Message);
            }

            if (sourceType == null)
                return BuildErrorDto("ElementType with ID " + sourceTypeIdValue.ToString(CultureInfo.InvariantCulture) + " not found.");

            var familyName = (sourceType as FamilySymbol)?.FamilyName ?? SafeFamilyNameFromType(sourceType);
            var category = SafeCategoryName(sourceType);

            // Check for existing name conflict before opening transaction
            if (TypeNameExistsInFamily(doc, sourceType, newTypeName))
            {
                return CommandResult.Ok(new
                {
                    duplicated = false,
                    new_type_id = (string)null,
                    new_type_name = newTypeName,
                    family_name = familyName,
                    category,
                    parameters_set = new JObject(),
                    error = "A type with the name '" + newTypeName + "' already exists in this family."
                });
            }

            ElementType newType = null;
            var parametersSet = new JObject();
            string txError = null;

            using (var tx = new Transaction(doc, "RvtMcp: duplicate type"))
            {
                try
                {
                    tx.Start();

                    try
                    {
                        newType = sourceType.Duplicate(newTypeName);
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        return CommandResult.Ok(new
                        {
                            duplicated = false,
                            new_type_id = (string)null,
                            new_type_name = newTypeName,
                            family_name = familyName,
                            category,
                            parameters_set = parametersSet,
                            error = "Duplicate failed: " + ex.Message
                        });
                    }

                    if (newType == null)
                    {
                        tx.RollBack();
                        return CommandResult.Ok(new
                        {
                            duplicated = false,
                            new_type_id = (string)null,
                            new_type_name = newTypeName,
                            family_name = familyName,
                            category,
                            parameters_set = parametersSet,
                            error = "Revit returned a null type from Duplicate()."
                        });
                    }

                    // Apply parameter overrides one by one
                    if (overrides != null)
                    {
                        foreach (var prop in overrides.Properties())
                        {
                            var paramName = prop.Name;
                            if (string.IsNullOrWhiteSpace(paramName))
                                continue;

                            var status = ApplyOverride(newType, paramName, prop.Value);
                            parametersSet[paramName] = status;
                        }
                    }

                    var status2 = tx.Commit();
                    if (status2 != TransactionStatus.Committed)
                        txError = "Revit did not commit the duplicate transaction. Transaction status: " + status2;
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted())
                    {
                        try { tx.RollBack(); }
                        catch { /* swallow */ }
                    }
                    return CommandResult.Ok(new
                    {
                        duplicated = false,
                        new_type_id = (string)null,
                        new_type_name = newTypeName,
                        family_name = familyName,
                        category,
                        parameters_set = parametersSet,
                        error = "Failed to duplicate type: " + ex.Message
                    });
                }
            }

            if (txError != null)
            {
                return CommandResult.Ok(new
                {
                    duplicated = false,
                    new_type_id = (string)null,
                    new_type_name = newTypeName,
                    family_name = familyName,
                    category,
                    parameters_set = parametersSet,
                    error = txError
                });
            }

            // Refresh family_name / category from the new type when possible
            var newFamilyName = (newType as FamilySymbol)?.FamilyName ?? familyName;
            var newCategory = SafeCategoryName(newType) ?? category;
            var newTypeIdString = RevitCompat.GetId(newType.Id).ToString(CultureInfo.InvariantCulture);
            var newTypeActualName = SafeName(newType) ?? newTypeName;

            return CommandResult.Ok(new
            {
                duplicated = true,
                new_type_id = newTypeIdString,
                new_type_name = newTypeActualName,
                family_name = newFamilyName,
                category = newCategory,
                parameters_set = parametersSet,
                error = (string)null
            });
        }

        private static CommandResult BuildErrorDto(string error)
        {
            return CommandResult.Ok(new
            {
                duplicated = false,
                new_type_id = (string)null,
                new_type_name = (string)null,
                family_name = (string)null,
                category = (string)null,
                parameters_set = new JObject(),
                error
            });
        }

        private static JToken ApplyOverride(ElementType typeElement, string parameterName, JToken value)
        {
            try
            {
                var parameter = FindParameter(typeElement, parameterName);
                if (parameter == null)
                    return new JValue("error: parameter not found");

                if (SafeIsReadOnly(parameter))
                    return new JValue("error: parameter is read-only");

                bool ok;
                string err;

                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        ok = SetString(parameter, value, out err);
                        break;
                    case StorageType.Integer:
                        ok = SetInteger(parameter, value, out err);
                        break;
                    case StorageType.Double:
                        ok = SetDouble(parameter, value, out err);
                        break;
                    case StorageType.ElementId:
                        ok = SetElementId(parameter, value, out err);
                        break;
                    default:
                        return new JValue("error: unsupported storage type " + parameter.StorageType);
                }

                if (!ok)
                    return new JValue("error: " + (err ?? "Revit rejected the value"));

                return new JValue("set");
            }
            catch (Exception ex)
            {
                return new JValue("error: " + ex.Message);
            }
        }

        private static bool SetString(Parameter parameter, JToken value, out string error)
        {
            error = null;
            var s = JTokenToString(value);
            if (s == null)
            {
                error = "value cannot be null for a String parameter.";
                return false;
            }
            if (!parameter.Set(s))
            {
                error = "Revit rejected the string value.";
                return false;
            }
            return true;
        }

        private static bool SetInteger(Parameter parameter, JToken value, out string error)
        {
            error = null;
            if (!TryToInt(value, out var parsed))
            {
                error = "value must be an integer.";
                return false;
            }
            if (!parameter.Set(parsed))
            {
                error = "Revit rejected the integer value.";
                return false;
            }
            return true;
        }

        private static bool SetDouble(Parameter parameter, JToken value, out string error)
        {
            error = null;
            if (!TryToDouble(value, out var parsed))
            {
                error = "value must be a number.";
                return false;
            }

            var internalValue = ConvertDoubleToInternal(parameter, parsed);
            if (!parameter.Set(internalValue))
            {
                error = "Revit rejected the double value.";
                return false;
            }
            return true;
        }

        private static bool SetElementId(Parameter parameter, JToken value, out string error)
        {
            error = null;
            if (!TryToLong(value, out var parsed))
            {
                error = "value must be an element id integer.";
                return false;
            }

            if (!RevitCompat.CanRepresentElementId(parsed))
            {
                error = RevitCompat.ElementIdRangeError(parsed);
                return false;
            }

            if (!parameter.Set(RevitCompat.ToElementId(parsed)))
            {
                error = "Revit rejected the element id value.";
                return false;
            }
            return true;
        }

        private static bool TryToInt(JToken value, out int result)
        {
            result = 0;
            if (value == null || value.Type == JTokenType.Null)
                return false;
            if (value.Type == JTokenType.Integer)
            {
                var lv = value.Value<long>();
                if (lv < int.MinValue || lv > int.MaxValue)
                    return false;
                result = (int)lv;
                return true;
            }
            if (value.Type == JTokenType.Float)
            {
                var d = value.Value<double>();
                if (d < int.MinValue || d > int.MaxValue)
                    return false;
                result = (int)d;
                return true;
            }
            if (value.Type == JTokenType.Boolean)
            {
                result = value.Value<bool>() ? 1 : 0;
                return true;
            }
            if (value.Type == JTokenType.String)
            {
                var s = value.Value<string>();
                if (string.IsNullOrWhiteSpace(s)) return false;
                return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            }
            return false;
        }

        private static bool TryToLong(JToken value, out long result)
        {
            result = 0;
            if (value == null || value.Type == JTokenType.Null)
                return false;
            if (value.Type == JTokenType.Integer)
            {
                result = value.Value<long>();
                return true;
            }
            if (value.Type == JTokenType.Float)
            {
                var d = value.Value<double>();
                if (d < long.MinValue || d > long.MaxValue)
                    return false;
                result = (long)d;
                return true;
            }
            if (value.Type == JTokenType.String)
            {
                var s = value.Value<string>();
                if (string.IsNullOrWhiteSpace(s)) return false;
                return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            }
            return false;
        }

        private static bool TryToDouble(JToken value, out double result)
        {
            result = 0;
            if (value == null || value.Type == JTokenType.Null)
                return false;
            if (value.Type == JTokenType.Integer)
            {
                result = (double)value.Value<long>();
                return true;
            }
            if (value.Type == JTokenType.Float)
            {
                result = value.Value<double>();
                return true;
            }
            if (value.Type == JTokenType.String)
            {
                var s = value.Value<string>();
                if (string.IsNullOrWhiteSpace(s)) return false;
                return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            }
            return false;
        }

        private static string JTokenToString(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null)
                return null;
            switch (value.Type)
            {
                case JTokenType.String:
                    return value.Value<string>();
                case JTokenType.Integer:
                    return value.Value<long>().ToString(CultureInfo.InvariantCulture);
                case JTokenType.Float:
                    return value.Value<double>().ToString("G17", CultureInfo.InvariantCulture);
                case JTokenType.Boolean:
                    return value.Value<bool>() ? "true" : "false";
                default:
                    return value.ToString(Formatting.None);
            }
        }

        private static double ConvertDoubleToInternal(Parameter parameter, double value)
        {
            switch (GetDoubleSpec(parameter))
            {
                case "length":
                    return value / 304.8;
                case "area":
                    return value / 0.09290304;
                case "volume":
                    return value / 0.02831685;
                case "angle":
                    return value * Math.PI / 180.0;
                default:
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

        private static string SafeName(Element element)
        {
            if (element == null) return null;
            try { return element.Name; }
            catch { return null; }
        }

        private static string SafeCategoryName(Element element)
        {
            if (element == null) return null;
            try { return element.Category?.Name; }
            catch { return null; }
        }

        private static string SafeFamilyNameFromType(ElementType typeElement)
        {
            if (typeElement == null) return null;
            try
            {
                // For system types, FamilyName is exposed on ElementType in modern API.
                return typeElement.FamilyName;
            }
            catch
            {
                return null;
            }
        }

        private static bool TypeNameExistsInFamily(Document doc, ElementType sourceType, string newName)
        {
            try
            {
                if (sourceType is FamilySymbol familySymbol)
                {
                    var family = familySymbol.Family;
                    if (family == null) return false;

                    foreach (var symbolIdObj in family.GetFamilySymbolIds())
                    {
                        var sibling = doc.GetElement(symbolIdObj) as FamilySymbol;
                        if (sibling == null) continue;
                        if (string.Equals(SafeName(sibling), newName, StringComparison.Ordinal))
                            return true;
                    }
                    return false;
                }

                // System type: scan other types of the same class
                var collector = new FilteredElementCollector(doc).OfClass(sourceType.GetType());
                foreach (var el in collector)
                {
                    if (el is ElementType et && string.Equals(SafeName(et), newName, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
            catch
            {
                // If we can't enumerate, defer to Duplicate() to throw and surface the error gracefully.
                return false;
            }
        }
    }
}
