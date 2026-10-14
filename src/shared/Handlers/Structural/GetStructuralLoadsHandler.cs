using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetStructuralLoadsHandler : IRevitCommand
    {
        public string Name => "get_structural_loads";
        public string Description => "Return structural loads (point/line/area). Optional filters: element_id (host), load_type ('point'|'line'|'area'). Returns id, type, host_id, force XYZ, moment XYZ, case info.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_id"":{""type"":""integer""},""load_type"":{""type"":""string"",""enum"":[""point"",""line"",""area""]},""limit"":{""type"":""integer"",""default"":500,""minimum"":1,""maximum"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var elementId = req.Value<long?>("element_id");
            var loadType = (req.Value<string>("load_type") ?? "").ToLowerInvariant();
            var limit = req.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 500)
                return CommandResult.Fail("limit must be between 1 and the hard maximum of 500.");

            var categories = new List<BuiltInCategory>();
            if (loadType == "point") categories.Add(BuiltInCategory.OST_PointLoads);
            else if (loadType == "line") categories.Add(BuiltInCategory.OST_LineLoads);
            else if (loadType == "area") categories.Add(BuiltInCategory.OST_AreaLoads);
            else
            {
                categories.Add(BuiltInCategory.OST_PointLoads);
                categories.Add(BuiltInCategory.OST_LineLoads);
                categories.Add(BuiltInCategory.OST_AreaLoads);
            }

            var items = new List<object>();
            foreach (var category in categories)
            {
                var loads = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToList();

                foreach (var load in loads)
                {
                    if (items.Count >= limit) break;

                    var hostId = TryGetLongParam(load, BuiltInParameter.HOST_ID_PARAM);
                    if (elementId.HasValue && hostId != elementId) continue;

                    items.Add(new
                    {
                        id = RevitCompat.GetId(load.Id),
                        load_kind = LoadKind(category),
                        host_id = hostId,
                        force_x = TryGetDoubleParam(load, BuiltInParameter.LOAD_FORCE_FX),
                        force_y = TryGetDoubleParam(load, BuiltInParameter.LOAD_FORCE_FY),
                        force_z = TryGetDoubleParam(load, BuiltInParameter.LOAD_FORCE_FZ),
                        moment_x = TryGetDoubleParam(load, BuiltInParameter.LOAD_MOMENT_MX),
                        moment_y = TryGetDoubleParam(load, BuiltInParameter.LOAD_MOMENT_MY),
                        moment_z = TryGetDoubleParam(load, BuiltInParameter.LOAD_MOMENT_MZ),
                        case_id = TryGetLongParam(load, BuiltInParameter.LOAD_CASE_ID),
                        case_name = TryGetStringParam(load, BuiltInParameter.LOAD_CASE_NAME)
                    });
                }
            }

            return CommandResult.Ok(new
            {
                count = items.Count,
                truncated = items.Count >= limit,
                items
            });
        }

        private static string LoadKind(BuiltInCategory category)
        {
            if (category == BuiltInCategory.OST_PointLoads) return "point";
            if (category == BuiltInCategory.OST_LineLoads) return "line";
            if (category == BuiltInCategory.OST_AreaLoads) return "area";
            return "unknown";
        }

        private static double? TryGetDoubleParam(Element element, BuiltInParameter builtInParameter)
        {
            try
            {
                var parameter = element.get_Parameter(builtInParameter);
                return parameter != null && parameter.HasValue ? parameter.AsDouble() : (double?)null;
            }
            catch
            {
                return null;
            }
        }

        private static long? TryGetLongParam(Element element, BuiltInParameter builtInParameter)
        {
            try
            {
                var parameter = element.get_Parameter(builtInParameter);
                if (parameter == null || !parameter.HasValue) return null;
                if (parameter.StorageType == StorageType.ElementId) return RevitCompat.GetIdOrNull(parameter.AsElementId());
                if (parameter.StorageType == StorageType.Integer) return parameter.AsInteger();
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetStringParam(Element element, BuiltInParameter builtInParameter)
        {
            try
            {
                var parameter = element.get_Parameter(builtInParameter);
                return parameter?.AsString() ?? parameter?.AsValueString();
            }
            catch
            {
                return null;
            }
        }
    }
}
