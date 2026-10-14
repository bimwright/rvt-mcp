using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListRebarHandler : IRevitCommand
    {
        public string Name => "list_rebar";
        public string Description => "List rebar instances. Optionally filter by host_id or view_id. Returns per-rebar: id, bar_type, diameter_mm, quantity, layout_rule, host_id, host_category.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""host_id"":{""type"":""integer""},""view_id"":{""type"":""integer""},""limit"":{""type"":""integer"",""default"":500,""minimum"":1,""maximum"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var hostId = req.Value<long?>("host_id");
            var viewId = req.Value<long?>("view_id");
            var limit = req.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 500)
                return CommandResult.Fail("limit must be between 1 and the hard maximum of 500.");

            FilteredElementCollector collector;
            if (viewId.HasValue)
                collector = new FilteredElementCollector(doc, RevitCompat.ToElementId(viewId.Value));
            else
                collector = new FilteredElementCollector(doc);

            var rebars = collector.OfClass(typeof(Rebar)).Cast<Rebar>().ToList();

            if (hostId.HasValue)
            {
                var hostElemId = RevitCompat.ToElementId(hostId.Value);
                rebars = rebars.Where(r => r.GetHostId() == hostElemId).ToList();
            }

            var items = new List<object>();
            foreach (var r in rebars.Take(limit))
            {
                var barType = doc.GetElement(r.GetTypeId()) as RebarBarType;
                var diameterMm = GetBarDiameterMm(barType);

                var host = doc.GetElement(r.GetHostId());

                items.Add(new
                {
                    id = RevitCompat.GetId(r.Id),
                    bar_type_id = RevitCompat.GetIdOrNull(r.GetTypeId()),
                    bar_type_name = barType?.Name,
                    diameter_mm = Math.Round(diameterMm, 2),
                    quantity = r.Quantity,
                    layout_rule = r.LayoutRule.ToString(),
                    host_id = RevitCompat.GetIdOrNull(r.GetHostId()),
                    host_category = host?.Category?.Name
                });
            }

            return CommandResult.Ok(new
            {
                count = items.Count,
                total_matched = rebars.Count,
                truncated = rebars.Count > limit,
                items
            });
        }

        private static double GetBarDiameterMm(RebarBarType barType)
        {
            if (barType == null) return 0;

            foreach (var propertyName in new[] { "BarDiameter", "BarNominalDiameter", "BarModelDiameter" })
            {
                try
                {
                    var property = barType.GetType().GetProperty(propertyName);
                    if (property == null) continue;
                    var value = property.GetValue(barType, null);
                    if (value is double feet && feet > 0) return feet * 304.8;
                }
                catch
                {
                    // Continue to the parameter fallback below.
                }
            }

            try
            {
                var parameter = barType.get_Parameter(BuiltInParameter.REBAR_MODEL_BAR_DIAMETER);
                var feet = parameter?.AsDouble() ?? 0;
                return feet > 0 ? feet * 304.8 : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
