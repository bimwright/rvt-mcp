using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetElementParametersHandler : IRevitCommand
    {
        public string Name => "get_element_parameters";
        public string Description => "Get instance parameters for one or more elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""includeReadOnly"":{""type"":""boolean"",""default"":true}},""required"":[""elementIds""]}";

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
            catch (Exception ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            var elementIds = request["elementIds"]?.ToObject<long[]>() ?? new long[0];
            var includeReadOnly = request.Value<bool?>("includeReadOnly") ?? true;

            if (elementIds.Length == 0)
                return CommandResult.Fail("elementIds array is required.");

            var elements = new JArray();
            var found = 0;
            var missing = 0;

            foreach (var id in elementIds)
            {
                var elementResult = new JObject
                {
                    ["elementId"] = id
                };

                Element element = null;
                try
                {
                    element = doc.GetElement(RevitCompat.ToElementId(id));
                }
                catch (Exception ex)
                {
                    elementResult["found"] = false;
                    elementResult["error"] = ex.Message;
                    missing++;
                    elements.Add(elementResult);
                    continue;
                }

                if (element == null)
                {
                    elementResult["found"] = false;
                    elementResult["error"] = "Element not found.";
                    missing++;
                    elements.Add(elementResult);
                    continue;
                }

                found++;
                elementResult["found"] = true;
                elementResult["name"] = element.Name;
                elementResult["category"] = element.Category?.Name;
                elementResult["typeId"] = ToJToken(RevitCompat.GetIdOrNull(element.GetTypeId()));

                var parameters = new JArray();
                var parameterErrors = new JArray();

                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter == null)
                        continue;

                    if (!includeReadOnly && SafeIsReadOnly(parameter))
                        continue;

                    try
                    {
                        parameters.Add(BuildParameterObject(doc, parameter));
                    }
                    catch (Exception ex)
                    {
                        parameterErrors.Add(new JObject
                        {
                            ["name"] = parameter.Definition?.Name,
                            ["error"] = ex.Message
                        });
                    }
                }

                elementResult["parameterCount"] = parameters.Count;
                elementResult["parameters"] = parameters;
                if (parameterErrors.Count > 0)
                    elementResult["parameterErrors"] = parameterErrors;

                elements.Add(elementResult);
            }

            return CommandResult.Ok(new
            {
                requested = elementIds.Length,
                found,
                missing,
                includeReadOnly,
                elements
            });
        }

        private static JObject BuildParameterObject(Document doc, Parameter parameter)
        {
            var definition = parameter.Definition;
            var parameterObject = new JObject
            {
                ["name"] = definition?.Name,
                ["id"] = ToJToken(TryGetParameterId(parameter)),
                ["builtInParameterId"] = ToJToken(TryGetBuiltInParameterId(definition)),
                ["builtInParameter"] = TryGetBuiltInParameterName(definition),
                ["storageType"] = parameter.StorageType.ToString(),
                ["isReadOnly"] = SafeIsReadOnly(parameter),
                ["hasValue"] = SafeHasValue(parameter)
            };

            var dataTypeId = TryGetDataTypeId(definition);
            parameterObject["dataType"] = dataTypeId;
            parameterObject["specId"] = dataTypeId;

            var rawValue = GetRawValue(doc, parameter, out var rawValueType, out var valueString, out var referencedElement);
            parameterObject["rawValueType"] = rawValueType;
            parameterObject["rawValue"] = rawValue;
            parameterObject["valueString"] = valueString;
            parameterObject["displayValue"] = TryGetDisplayValue(parameter);

            if (referencedElement != null)
                parameterObject["referencedElement"] = referencedElement;

            return parameterObject;
        }

        private static JToken GetRawValue(
            Document doc,
            Parameter parameter,
            out string rawValueType,
            out string valueString,
            out JObject referencedElement)
        {
            referencedElement = null;
            valueString = null;

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    rawValueType = "string";
                    var stringValue = parameter.AsString();
                    valueString = stringValue;
                    return stringValue == null ? JValue.CreateNull() : new JValue(stringValue);

                case StorageType.Integer:
                    rawValueType = "int";
                    var intValue = parameter.AsInteger();
                    valueString = intValue.ToString(CultureInfo.InvariantCulture);
                    return new JValue(intValue);

                case StorageType.Double:
                    rawValueType = "double";
                    var doubleValue = parameter.AsDouble();
                    valueString = doubleValue.ToString("R", CultureInfo.InvariantCulture);
                    return new JValue(doubleValue);

                case StorageType.ElementId:
                    rawValueType = "elementId";
                    var elementId = parameter.AsElementId();
                    var idValue = RevitCompat.GetIdOrNull(elementId);
                    valueString = idValue?.ToString(CultureInfo.InvariantCulture);
                    referencedElement = GetReferencedElement(doc, elementId);
                    return ToJToken(idValue);

                default:
                    rawValueType = parameter.StorageType.ToString();
                    return JValue.CreateNull();
            }
        }

        private static JObject GetReferencedElement(Document doc, ElementId elementId)
        {
            if (doc == null || elementId == null)
                return null;

            try
            {
                var referenced = doc.GetElement(elementId);
                if (referenced == null)
                    return null;

                return new JObject
                {
                    ["elementId"] = ToJToken(RevitCompat.GetIdOrNull(referenced.Id)),
                    ["name"] = referenced.Name,
                    ["category"] = referenced.Category?.Name
                };
            }
            catch
            {
                return null;
            }
        }

        private static long? TryGetParameterId(Parameter parameter)
        {
            try
            {
                return RevitCompat.GetIdOrNull(parameter?.Id);
            }
            catch
            {
                return null;
            }
        }

        private static int? TryGetBuiltInParameterId(Definition definition)
        {
            try
            {
                var internalDefinition = definition as InternalDefinition;
                if (internalDefinition == null)
                    return null;

                var builtInParameter = internalDefinition.BuiltInParameter;
                if (builtInParameter == BuiltInParameter.INVALID)
                    return null;

                return (int)builtInParameter;
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetBuiltInParameterName(Definition definition)
        {
            try
            {
                var internalDefinition = definition as InternalDefinition;
                if (internalDefinition == null)
                    return null;

                var builtInParameter = internalDefinition.BuiltInParameter;
                return builtInParameter == BuiltInParameter.INVALID ? null : builtInParameter.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetDataTypeId(Definition definition)
        {
            try
            {
                var dataType = definition?.GetDataType();
                var typeId = dataType?.TypeId;
                return string.IsNullOrWhiteSpace(typeId) ? null : typeId;
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetDisplayValue(Parameter parameter)
        {
            try
            {
                var value = parameter.AsValueString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch
            {
                return null;
            }
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

        private static bool SafeHasValue(Parameter parameter)
        {
            try
            {
                return parameter?.HasValue ?? false;
            }
            catch
            {
                return false;
            }
        }

        private static JToken ToJToken(long? value)
        {
            return value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
        }

        private static JToken ToJToken(int? value)
        {
            return value.HasValue ? new JValue(value.Value) : JValue.CreateNull();
        }
    }
}
