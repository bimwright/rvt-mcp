using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetElementDetailsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "get_element_details";
        public string Description => "Get detailed metadata, location, and bounding box information for Revit elements by ID.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}}},""required"":[""elementIds""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long[] elementIds;
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                elementIds = request["elementIds"]?.ToObject<long[]>() ?? new long[0];
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            if (elementIds.Length == 0)
                return CommandResult.Fail("elementIds array is required.");

            var elements = new List<object>();
            var errors = new List<object>();

            foreach (var requestedId in elementIds)
            {
                try
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(requestedId));
                    if (element == null)
                    {
                        errors.Add(new { elementId = requestedId, error = "Element not found." });
                        continue;
                    }

                    elements.Add(BuildElementDetails(doc, element));
                }
                catch (Exception ex)
                {
                    errors.Add(new { elementId = requestedId, error = ex.Message });
                }
            }

            return CommandResult.Ok(new
            {
                requested = elementIds.Length,
                returned = elements.Count,
                elements,
                errors
            });
        }

        private static object BuildElementDetails(Document doc, Element element)
        {
            var typeId = GetOptionalId(SafeElementId(() => element.GetTypeId()));
            var typeElement = GetElementById(doc, typeId);

            var levelId = GetOptionalId(SafeElementId(() => element.LevelId));
            var levelElement = GetElementById(doc, levelId);

            var groupId = GetOptionalId(SafeElementId(() => element.GroupId));
            var assemblyInstanceId = GetOptionalId(SafeElementId(() => element.AssemblyInstanceId));

            var ownerViewId = GetOptionalId(SafeElementId(() => element.OwnerViewId));
            var ownerViewElement = GetElementById(doc, ownerViewId);

            var phaseCreatedId = GetOptionalId(SafeElementId(() => element.CreatedPhaseId));
            var phaseCreatedElement = GetElementById(doc, phaseCreatedId);

            var phaseDemolishedId = GetOptionalId(SafeElementId(() => element.DemolishedPhaseId));
            var phaseDemolishedElement = GetElementById(doc, phaseDemolishedId);

            var designOption = Safe(() => element.DesignOption);
            var workset = GetWorksetInfo(doc, element);

            return new
            {
                elementId = RevitCompat.GetId(element.Id),
                uniqueId = Safe(() => element.UniqueId),
                name = GetElementName(element),
                category = Safe(() => element.Category?.Name),
                categoryId = RevitCompat.GetIdOrNull(SafeElementId(() => element.Category?.Id)),
                typeId,
                typeName = GetElementName(typeElement),
                levelId,
                levelName = GetElementName(levelElement),
                groupId,
                assemblyInstanceId,
                worksetId = workset.Id,
                worksetName = workset.Name,
                phaseCreated = phaseCreatedId.HasValue
                    ? (object)new { id = phaseCreatedId, name = GetElementName(phaseCreatedElement) }
                    : null,
                phaseDemolished = phaseDemolishedId.HasValue
                    ? (object)new { id = phaseDemolishedId, name = GetElementName(phaseDemolishedElement) }
                    : null,
                pinned = SafeBool(() => element.Pinned),
                designOptionId = designOption == null ? null : GetOptionalId(designOption.Id),
                designOptionName = GetElementName(designOption),
                ownerViewId,
                ownerViewName = GetElementName(ownerViewElement),
                location = GetLocationSummary(element),
                boundingBox = GetBoundingBoxSummary(element)
            };
        }

        private static Element GetElementById(Document doc, long? id)
        {
            if (!id.HasValue)
                return null;

            try
            {
                return doc.GetElement(RevitCompat.ToElementId(id.Value));
            }
            catch
            {
                return null;
            }
        }

        private static WorksetInfo GetWorksetInfo(Document doc, Element element)
        {
            WorksetId worksetId = null;
            try
            {
                worksetId = element.WorksetId;
            }
            catch
            {
                return new WorksetInfo();
            }

            if (worksetId == null || worksetId.IntegerValue <= 0)
                return new WorksetInfo();

            string worksetName = null;
            try
            {
                worksetName = doc.GetWorksetTable()?.GetWorkset(worksetId)?.Name;
            }
            catch
            {
                worksetName = null;
            }

            return new WorksetInfo
            {
                Id = worksetId.IntegerValue,
                Name = worksetName
            };
        }

        private static object GetLocationSummary(Element element)
        {
            var location = Safe(() => element.Location);
            if (location == null)
                return null;

            var pointLocation = location as LocationPoint;
            if (pointLocation != null)
            {
                var point = Safe(() => pointLocation.Point);
                if (point == null)
                    return new { type = "point", unit = "mm", point = (object)null };

                return new
                {
                    type = "point",
                    unit = "mm",
                    point = ToMmPoint(point)
                };
            }

            var curveLocation = location as LocationCurve;
            if (curveLocation != null)
            {
                var curve = Safe(() => curveLocation.Curve);
                if (curve == null)
                    return new { type = "curve", unit = "mm", start = (object)null, end = (object)null };

                XYZ start = null;
                XYZ end = null;
                try
                {
                    if (curve.IsBound)
                    {
                        start = curve.GetEndPoint(0);
                        end = curve.GetEndPoint(1);
                    }
                }
                catch
                {
                    start = null;
                    end = null;
                }

                return new
                {
                    type = "curve",
                    unit = "mm",
                    start = start == null ? null : ToMmPoint(start),
                    end = end == null ? null : ToMmPoint(end),
                    length = SafeDouble(() => curve.Length * FeetToMm)
                };
            }

            return new
            {
                type = location.GetType().Name,
                unit = "mm"
            };
        }

        private static object GetBoundingBoxSummary(Element element)
        {
            BoundingBoxXYZ bbox = null;
            try
            {
                bbox = element.get_BoundingBox(null);
            }
            catch
            {
                bbox = null;
            }

            if (bbox == null)
                return null;

            return new
            {
                unit = "mm",
                min = ToMmPoint(bbox.Min),
                max = ToMmPoint(bbox.Max)
            };
        }

        private static object ToMmPoint(XYZ point)
        {
            if (point == null)
                return null;

            return new
            {
                x = Math.Round(point.X * FeetToMm, 3),
                y = Math.Round(point.Y * FeetToMm, 3),
                z = Math.Round(point.Z * FeetToMm, 3)
            };
        }

        private static long? GetOptionalId(ElementId id)
        {
            if (id == null)
                return null;

            var value = RevitCompat.GetId(id);
            if (value == RevitCompat.GetId(ElementId.InvalidElementId))
                return null;

            return value;
        }

        private static string GetElementName(Element element)
        {
            if (element == null)
                return null;

            return Safe(() => element.Name);
        }

        private static ElementId SafeElementId(Func<ElementId> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return null;
            }
        }

        private static bool? SafeBool(Func<bool> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return null;
            }
        }

        private static double? SafeDouble(Func<double> getter)
        {
            try
            {
                return Math.Round(getter(), 3);
            }
            catch
            {
                return null;
            }
        }

        private static T Safe<T>(Func<T> getter)
        {
            try
            {
                return getter();
            }
            catch
            {
                return default(T);
            }
        }

        private class WorksetInfo
        {
            public long? Id { get; set; }
            public string Name { get; set; }
        }
    }
}
