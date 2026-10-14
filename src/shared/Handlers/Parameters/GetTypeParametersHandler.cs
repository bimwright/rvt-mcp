using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetTypeParametersHandler : IRevitCommand
    {
        public string Name => "get_type_parameters";
        public string Description => "Get type metadata and type parameters for explicit type IDs and/or the types resolved from element IDs.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Optional element IDs whose type IDs will be resolved via Element.GetTypeId().""},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Optional Revit element type IDs to inspect directly.""}}}";

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

            if (!TryReadIdArray(request, "elementIds", out var elementIds, out var elementIdsError))
                return CommandResult.Fail(elementIdsError);
            if (!TryReadIdArray(request, "typeIds", out var explicitTypeIds, out var typeIdsError))
                return CommandResult.Fail(typeIdsError);

            if (elementIds.Count == 0 && explicitTypeIds.Count == 0)
                return CommandResult.Fail("Provide at least one ID in elementIds or typeIds.");

            var orderedTypeIds = new List<long>();
            var sourceElementIdsByType = new Dictionary<long, List<long>>();
            var missingElementIds = new List<long>();
            var elementsWithoutType = new List<long>();
            var missingTypeIds = new List<long>();

            foreach (var elementId in elementIds)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(elementId));
                if (element == null)
                {
                    missingElementIds.Add(elementId);
                    continue;
                }

                var typeId = element.GetTypeId();
                if (!IsValidElementId(typeId))
                {
                    elementsWithoutType.Add(elementId);
                    continue;
                }

                var typeIdValue = RevitCompat.GetId(typeId);
                AddTypeId(orderedTypeIds, typeIdValue);
                AddSourceElementId(sourceElementIdsByType, typeIdValue, elementId);
            }

            foreach (var typeId in explicitTypeIds)
                AddTypeId(orderedTypeIds, typeId);

            var types = new List<object>();
            foreach (var typeId in orderedTypeIds)
            {
                var typeElement = doc.GetElement(RevitCompat.ToElementId(typeId)) as ElementType;
                if (typeElement == null)
                {
                    missingTypeIds.Add(typeId);
                    continue;
                }

                var familySymbol = typeElement as FamilySymbol;
                var sourceElementIds = sourceElementIdsByType.TryGetValue(typeId, out var sourceIds)
                    ? sourceIds.ToArray()
                    : new long[0];

                types.Add(new
                {
                    typeId = RevitCompat.GetId(typeElement.Id),
                    typeName = typeElement.Name,
                    familyName = familySymbol?.FamilyName,
                    category = typeElement.Category?.Name,
                    sourceElementIds,
                    parameters = GetParameterDtos(doc, typeElement)
                });
            }

            return CommandResult.Ok(new
            {
                count = types.Count,
                types,
                missingElementIds,
                elementsWithoutType,
                missingTypeIds
            });
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

        private static object[] GetParameterDtos(Document doc, ElementType typeElement)
        {
            var parameters = new List<object>();

            foreach (Parameter parameter in typeElement.Parameters)
            {
                if (parameter == null)
                    continue;

                var valueString = GetValueString(parameter);
                var raw = GetRawValue(parameter);

                parameters.Add(new
                {
                    name = parameter.Definition?.Name,
                    storage = parameter.StorageType.ToString(),
                    readOnly = parameter.IsReadOnly,
                    valueString,
                    raw,
                    display = GetDisplayValue(doc, parameter, valueString),
                    dataType = GetDataType(parameter)
                });
            }

            return parameters.ToArray();
        }

        private static string GetValueString(Parameter parameter)
        {
            try
            {
                return parameter.AsValueString();
            }
            catch
            {
                return null;
            }
        }

        private static object GetRawValue(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString();
                    case StorageType.Integer:
                        return parameter.AsInteger();
                    case StorageType.Double:
                        return parameter.AsDouble();
                    case StorageType.ElementId:
                        return RevitCompat.GetId(parameter.AsElementId());
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GetDisplayValue(Document doc, Parameter parameter, string valueString)
        {
            if (!string.IsNullOrEmpty(valueString))
                return valueString;

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

        private static string GetDataType(Parameter parameter)
        {
            try
            {
                var dataType = parameter.Definition?.GetDataType();
                if (dataType == null)
                    return null;

                return string.IsNullOrEmpty(dataType.TypeId)
                    ? dataType.ToString()
                    : dataType.TypeId;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsValidElementId(ElementId id)
        {
            return id != null && RevitCompat.GetId(id) != RevitCompat.GetId(ElementId.InvalidElementId);
        }
    }
}
