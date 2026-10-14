using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ChangeElementTypeHandler : IRevitCommand
    {
        public string Name => "change_element_type";
        public string Description => "Change the type of one or more Revit elements.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""typeId"":{""type"":""integer""}},""required"":[""elementIds"",""typeId""]}";

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

            if (elementIds.Count == 0)
                return CommandResult.Fail("elementIds array is required.");

            if (!TryReadRequiredId(request, "typeId", out var requestedTypeId, out var typeIdError))
                return CommandResult.Fail(typeIdError);

            var targetTypeElementId = RevitCompat.ToElementId(requestedTypeId);
            var targetType = doc.GetElement(targetTypeElementId) as ElementType;
            if (targetType == null)
                return CommandResult.Fail("ElementType with ID " + requestedTypeId.ToString(CultureInfo.InvariantCulture) + " not found.");

            var targetTypeInfo = GetTypeSummary(targetType);
            var changed = new List<object>();
            var failed = new List<object>();
            var errors = new List<object>();

            using (var tx = new Transaction(doc, "MCP: Change element type"))
            {
                tx.Start();

                foreach (var elementId in elementIds)
                {
                    TypeSummary oldTypeInfo = null;
                    try
                    {
                        var element = doc.GetElement(RevitCompat.ToElementId(elementId));
                        if (element == null)
                        {
                            AddFailure(failed, errors, elementId, null, targetTypeInfo, "Element not found.");
                            continue;
                        }

                        oldTypeInfo = GetElementTypeSummary(doc, element);

                        bool isValidType;
                        try
                        {
                            isValidType = element.IsValidType(targetTypeElementId);
                        }
                        catch (Exception ex)
                        {
                            AddFailure(failed, errors, elementId, oldTypeInfo, targetTypeInfo, "IsValidType failed: " + ex.Message);
                            continue;
                        }

                        if (!isValidType)
                        {
                            AddFailure(failed, errors, elementId, oldTypeInfo, targetTypeInfo, "Target type is not valid for this element.");
                            continue;
                        }

                        var replacementId = element.ChangeTypeId(targetTypeElementId);
                        var resultingElementId = IsValidElementId(replacementId)
                            ? RevitCompat.GetId(replacementId)
                            : elementId;

                        changed.Add(new
                        {
                            elementId,
                            resultingElementId,
                            oldTypeId = oldTypeInfo?.Id,
                            oldTypeName = oldTypeInfo?.Name,
                            newTypeId = targetTypeInfo.Id,
                            newTypeName = targetTypeInfo.Name
                        });
                    }
                    catch (Exception ex)
                    {
                        AddFailure(failed, errors, elementId, oldTypeInfo, targetTypeInfo, ex.Message);
                    }
                }

                var status = tx.Commit();
                if (status != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit element type changes. Transaction status: " + status);
            }

            return CommandResult.Ok(new
            {
                requested = elementIds.Count,
                changedCount = changed.Count,
                failedCount = failed.Count,
                changed,
                failed,
                errors
            });
        }

        private static bool TryReadIdArray(JObject request, string propertyName, out List<long> ids, out string error)
        {
            ids = new List<long>();
            error = null;

            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = propertyName + " array is required.";
                return false;
            }

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

        private static bool TryReadRequiredId(JObject request, string propertyName, out long id, out string error)
        {
            id = 0;
            error = null;

            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = propertyName + " is required.";
                return false;
            }

            if (token.Type != JTokenType.Integer)
            {
                error = propertyName + " must be an integer.";
                return false;
            }

            id = token.Value<long>();
            if (!RevitCompat.CanRepresentElementId(id))
            {
                error = propertyName + " " + RevitCompat.ElementIdRangeError(id);
                return false;
            }

            return true;
        }

        private static void AddFailure(
            List<object> failed,
            List<object> errors,
            long elementId,
            TypeSummary oldTypeInfo,
            TypeSummary newTypeInfo,
            string error)
        {
            var failure = new
            {
                elementId,
                oldTypeId = oldTypeInfo?.Id,
                oldTypeName = oldTypeInfo?.Name,
                newTypeId = newTypeInfo?.Id,
                newTypeName = newTypeInfo?.Name,
                error
            };

            failed.Add(failure);
            errors.Add(failure);
        }

        private static TypeSummary GetElementTypeSummary(Document doc, Element element)
        {
            if (element == null)
                return null;

            ElementId typeId;
            try
            {
                typeId = element.GetTypeId();
            }
            catch
            {
                return null;
            }

            if (!IsValidElementId(typeId))
                return null;

            return GetTypeSummary(doc.GetElement(typeId) as ElementType, typeId);
        }

        private static TypeSummary GetTypeSummary(ElementType typeElement)
        {
            return GetTypeSummary(typeElement, typeElement?.Id);
        }

        private static TypeSummary GetTypeSummary(ElementType typeElement, ElementId fallbackId)
        {
            if (typeElement == null && !IsValidElementId(fallbackId))
                return null;

            return new TypeSummary
            {
                Id = typeElement != null
                    ? RevitCompat.GetId(typeElement.Id)
                    : RevitCompat.GetId(fallbackId),
                Name = SafeName(typeElement)
            };
        }

        private static string SafeName(Element element)
        {
            if (element == null)
                return null;

            try
            {
                return element.Name;
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

        private class TypeSummary
        {
            public long? Id { get; set; }
            public string Name { get; set; }
        }
    }
}
