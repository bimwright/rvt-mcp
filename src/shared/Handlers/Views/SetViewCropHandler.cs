using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewCropHandler : IRevitCommand
    {
        public string Name => "set_view_crop";
        public string Description => "Modify view crop box: toggle active/visible, set explicit bounds (mm), or fit to a list of element ids with optional padding_mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""enabled"":{""type"":""boolean""},""visible"":{""type"":""boolean""},""bounds"":{""type"":""object"",""properties"":{""min_x_mm"":{""type"":""number""},""min_y_mm"":{""type"":""number""},""min_z_mm"":{""type"":""number""},""max_x_mm"":{""type"":""number""},""max_y_mm"":{""type"":""number""},""max_z_mm"":{""type"":""number""}}},""fit_element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""padding_mm"":{""type"":""number"",""default"":100}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var enabled = req.Value<bool?>("enabled");
            var visible = req.Value<bool?>("visible");
            var boundsObj = req["bounds"] as JObject;
            var fitIdsToken = req["fit_element_ids"] as JArray;
            var paddingMm = req.Value<double?>("padding_mm") ?? 100;

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");
            if (view.IsTemplate) return CommandResult.Fail("Cannot modify a view template.");

            using (var tx = new Transaction(doc, "RvtMcp: Set view crop"))
            {
                tx.Start();
                try
                {
                    if (enabled.HasValue) view.CropBoxActive = enabled.Value;
                    if (visible.HasValue) view.CropBoxVisible = visible.Value;

                    var newBox = BuildExplicitBox(boundsObj) ?? BuildFitBox(doc, view, fitIdsToken, paddingMm);
                    if (newBox != null) view.CropBox = newBox;

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        view_id = RevitCompat.GetId(view.Id),
                        crop_active = view.CropBoxActive,
                        crop_visible = view.CropBoxVisible,
                        bounds_updated = newBox != null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to set view crop: {ex.Message}");
                }
            }
        }

        private static BoundingBoxXYZ BuildExplicitBox(JObject bounds)
        {
            if (bounds == null) return null;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(
                    (bounds.Value<double?>("min_x_mm") ?? 0) / 304.8,
                    (bounds.Value<double?>("min_y_mm") ?? 0) / 304.8,
                    (bounds.Value<double?>("min_z_mm") ?? 0) / 304.8),
                Max = new XYZ(
                    (bounds.Value<double?>("max_x_mm") ?? 0) / 304.8,
                    (bounds.Value<double?>("max_y_mm") ?? 0) / 304.8,
                    (bounds.Value<double?>("max_z_mm") ?? 0) / 304.8)
            };
        }

        private static BoundingBoxXYZ BuildFitBox(Document doc, View view, JArray fitIdsToken, double paddingMm)
        {
            if (fitIdsToken == null || !fitIdsToken.Any()) return null;

            var ids = fitIdsToken.Select(t => RevitCompat.ToElementId(t.Value<long>())).ToList();
            XYZ min = null;
            XYZ max = null;

            foreach (var id in ids)
            {
                var box = doc.GetElement(id)?.get_BoundingBox(view);
                if (box == null) continue;

                min = min == null
                    ? box.Min
                    : new XYZ(Math.Min(min.X, box.Min.X), Math.Min(min.Y, box.Min.Y), Math.Min(min.Z, box.Min.Z));
                max = max == null
                    ? box.Max
                    : new XYZ(Math.Max(max.X, box.Max.X), Math.Max(max.Y, box.Max.Y), Math.Max(max.Z, box.Max.Z));
            }

            if (min == null || max == null) return null;

            var padding = paddingMm / 304.8;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(min.X - padding, min.Y - padding, min.Z - padding),
                Max = new XYZ(max.X + padding, max.Y + padding, max.Z + padding)
            };
        }
    }
}
