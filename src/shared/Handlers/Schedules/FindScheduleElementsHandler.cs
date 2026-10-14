using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class FindScheduleElementsHandler : IRevitCommand
    {
        private const int MaxParametersPerElement = 30;

        public string Name => "find_schedule_elements";
        public string Description => "Find Revit elements aggregated by a schedule (using FilteredElementCollector scoped to the schedule's id). Returns count grouped by category and per-element {id, name, category, typeName}. Optional includeParameters returns each element's visible parameters with unit-corrected values.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""groupByCategory"":{""type"":""boolean"",""default"":true},""includeParameters"":{""type"":""boolean"",""default"":false},""limit"":{""type"":""integer"",""default"":500,""minimum"":1,""maximum"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var scheduleId = request.Value<long?>("scheduleId");
            var scheduleName = request.Value<string>("scheduleName");
            var groupByCategory = request.Value<bool?>("groupByCategory") ?? true;
            var includeParameters = request.Value<bool?>("includeParameters") ?? false;
            var limit = request.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 500)
                return CommandResult.Fail("limit must be between 1 and the hard maximum of 500.");

            if (scheduleId == null && string.IsNullOrEmpty(scheduleName))
                return CommandResult.Fail("Either scheduleId or scheduleName is required.");

            // Resolve schedule
            ViewSchedule schedule = null;
            var allSchedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsTemplate)
                .ToList();

            if (scheduleId != null)
            {
                schedule = allSchedules.FirstOrDefault(vs => RevitCompat.GetId(vs.Id) == scheduleId.Value);
                if (schedule == null)
                    return CommandResult.Fail($"Schedule with id {scheduleId.Value} not found.");
            }
            else
            {
                var matches = allSchedules.Where(vs =>
                    string.Equals(vs.Name, scheduleName, StringComparison.OrdinalIgnoreCase));
                var matchList = matches.ToList();
                if (matchList.Count > 1)
                    return CommandResult.Fail($"Ambiguous schedule name '{scheduleName}': {matchList.Count} matches found. Use scheduleId.");
                schedule = matchList.FirstOrDefault();
                if (schedule == null)
                    return CommandResult.Fail($"Schedule '{scheduleName}' not found.");
            }

            // Collector scoped to the schedule's id returns elements aggregated by that schedule
            var all = new FilteredElementCollector(doc, schedule.Id).ToElements();
            var totalCount = all.Count;

            Dictionary<string, int> byCategory = null;
            if (groupByCategory)
            {
                byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var el in all)
                {
                    var catName = el.Category?.Name ?? "<none>";
                    if (byCategory.ContainsKey(catName))
                        byCategory[catName]++;
                    else
                        byCategory[catName] = 1;
                }
            }

            var elementsOut = new List<object>();
            var take = Math.Min(limit, totalCount);
            for (var i = 0; i < take; i++)
            {
                var el = all[i];
                try
                {
                    var typeEl = doc.GetElement(el.GetTypeId());
                    if (includeParameters)
                    {
                        var paramMap = new Dictionary<string, string>(StringComparer.Ordinal);
                        var paramCount = 0;
                        foreach (Parameter p in el.Parameters)
                        {
                            if (paramCount >= MaxParametersPerElement) break;
                            if (p == null || p.Definition == null || !p.HasValue) continue;
                            try
                            {
                                var pName = p.Definition.Name;
                                if (string.IsNullOrEmpty(pName)) continue;
                                if (paramMap.ContainsKey(pName)) continue;
                                var pVal = GetParamValueAsString(doc, p);
                                if (pVal == null) continue;
                                paramMap[pName] = pVal;
                                paramCount++;
                            }
                            catch { }
                        }

                        elementsOut.Add(new
                        {
                            id = RevitCompat.GetId(el.Id),
                            name = el.Name ?? "",
                            categoryName = el.Category?.Name ?? "<none>",
                            typeName = typeEl?.Name ?? "",
                            parameters = paramMap
                        });
                    }
                    else
                    {
                        elementsOut.Add(new
                        {
                            id = RevitCompat.GetId(el.Id),
                            name = el.Name ?? "",
                            categoryName = el.Category?.Name ?? "<none>",
                            typeName = typeEl?.Name ?? ""
                        });
                    }
                }
                catch (Exception ex)
                {
                    elementsOut.Add(new
                    {
                        id = RevitCompat.GetId(el.Id),
                        _error = ex.Message
                    });
                }
            }

            var truncated = totalCount > limit;

            return CommandResult.Ok(new
            {
                scheduleId = RevitCompat.GetId(schedule.Id),
                scheduleName = schedule.Name,
                totalCount = totalCount,
                returned = elementsOut.Count,
                truncated = truncated,
                byCategory = byCategory,
                elements = elementsOut
            });
        }

        private static double ConvertToDisplayUnits(Document doc, Parameter param, double internalValue)
        {
            try
            {
                var specId = param.Definition.GetDataType();

                if (specId == SpecTypeId.Length || specId == SpecTypeId.PipeSize ||
                    specId == SpecTypeId.DuctSize || specId == SpecTypeId.BarDiameter ||
                    specId == SpecTypeId.WireDiameter || specId == SpecTypeId.SectionDimension)
                    return internalValue * 304.8; // feet → mm
                if (specId == SpecTypeId.Area)
                    return internalValue * 0.092903; // sq feet → m²
                if (specId == SpecTypeId.Volume)
                    return internalValue * 0.0283168; // cu feet → m³
                if (specId == SpecTypeId.Angle)
                    return internalValue * (180.0 / Math.PI); // radians → degrees
            }
            catch { }

            // Unknown spec type — return raw value
            return internalValue;
        }

        private static string GetParamValueAsString(Document doc, Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    var raw = param.AsDouble();
                    var display = ConvertToDisplayUnits(doc, param, raw);
                    return Math.Round(display, 2).ToString();
                case StorageType.ElementId:
                    var refEl = doc.GetElement(param.AsElementId());
                    return refEl?.Name ?? RevitCompat.GetId(param.AsElementId()).ToString();
                default:
                    return null;
            }
        }
    }
}
