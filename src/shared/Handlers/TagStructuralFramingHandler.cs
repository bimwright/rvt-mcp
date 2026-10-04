using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class TagStructuralFramingHandler : IRevitCommand
    {
        public string Name => "tag_structural_framing";
        public string Description => "Place structural framing tags on structural beams in the active or specified view. Uses default tag type unless tag_type_id provided.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""tag_type_id"":{""type"":""integer""},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var tagTypeIdParam = req.Value<long?>("tag_type_id");
            var elementIdsToken = req["element_ids"] as JArray;

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");

            FamilySymbol tagType;
            if (tagTypeIdParam.HasValue)
            {
                tagType = doc.GetElement(RevitCompat.ToElementId(tagTypeIdParam.Value)) as FamilySymbol;
            }
            else
            {
                tagType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_StructuralFramingTags)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();
            }

            if (tagType == null)
                return CommandResult.Fail("No StructuralFramingTags family loaded in project.");

            List<Element> elements;
            if (elementIdsToken != null && elementIdsToken.Any())
            {
                elements = elementIdsToken
                    .Select(t => doc.GetElement(RevitCompat.ToElementId(t.Value<long>())))
                    .Where(IsStructuralFraming)
                    .ToList();
            }
            else
            {
                elements = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType()
                    .ToList();
            }

            var tagged = 0;
            var skipped = 0;
            using (var tx = new Transaction(doc, "RvtMcp: Tag structural framing"))
            {
                tx.Start();
                try
                {
                    if (!tagType.IsActive) tagType.Activate();
                    doc.Regenerate();

                    foreach (var element in elements)
                    {
                        try
                        {
                            var point = GetTagPoint(element, view);
                            if (point == null)
                            {
                                skipped++;
                                continue;
                            }

                            var tag = IndependentTag.Create(
                                doc,
                                view.Id,
                                new Reference(element),
                                false,
                                TagMode.TM_ADDBY_CATEGORY,
                                TagOrientation.Horizontal,
                                point);

                            if (tag == null)
                            {
                                skipped++;
                                continue;
                            }

                            tag.ChangeTypeId(tagType.Id);
                            tagged++;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to tag structural framing: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id),
                tag_type_id = RevitCompat.GetId(tagType.Id),
                tagged,
                skipped,
                total_candidates = elements.Count
            });
        }

        private static bool IsStructuralFraming(Element element)
        {
            if (element == null || element.Category == null) return false;
            return RevitCompat.GetId(element.Category.Id) == (long)BuiltInCategory.OST_StructuralFraming;
        }

        private static XYZ GetTagPoint(Element element, View view)
        {
            if (element.Location is LocationCurve locationCurve)
                return locationCurve.Curve.Evaluate(0.5, true);
            if (element.Location is LocationPoint locationPoint)
                return locationPoint.Point;

            var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            return box == null ? null : (box.Min + box.Max) * 0.5;
        }
    }
}
