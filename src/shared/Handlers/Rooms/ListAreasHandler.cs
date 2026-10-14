using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListAreasHandler : IRevitCommand
    {
        public string Name => "list_areas";
        public string Description => "List areas across area schemes and area plans";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""properties"": {
                ""area_scheme_name"": { ""type"": ""string"" },
                ""level_name"": { ""type"": ""string"" },
                ""status"": { ""type"": ""string"", ""enum"": [""all"", ""placed"", ""not_enclosed""] },
                ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 20000 }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            string areaSchemeFilter = "";
            string levelFilter = "";
            string statusFilter = "all";
            int limit = 5000;

            try
            {
                if (!string.IsNullOrEmpty(paramsJson))
                {
                    var request = JObject.Parse(paramsJson);
                    if (request.TryGetValue("area_scheme_name", out var schemeVal))
                        areaSchemeFilter = schemeVal.Value<string>() ?? "";
                    if (request.TryGetValue("level_name", out var lvVal))
                        levelFilter = lvVal.Value<string>() ?? "";
                    if (request.TryGetValue("status", out var stVal))
                        statusFilter = stVal.Value<string>() ?? "all";
                    if (request.TryGetValue("limit", out var limVal))
                        limit = limVal.Value<int>();
                }
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            if (limit < 1 || limit > 20000)
                return CommandResult.Fail("Limit must be between 1 and 20000.");

            var allAreas = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Areas)
                .WhereElementIsNotElementType()
                .Cast<Area>()
                .ToList();

            var areasList = new List<object>();
            int placedCount = 0;
            int notEnclosedCount = 0;
            int totalMatching = 0;

            foreach (var a in allAreas)
            {
                // Resolve Plan View & Area Scheme
                ViewPlan viewPlan = null;
                if (a.OwnerViewId != ElementId.InvalidElementId)
                {
                    viewPlan = doc.GetElement(a.OwnerViewId) as ViewPlan;
                }

                var scheme = viewPlan?.AreaScheme;
                var level = a.Level ?? viewPlan?.GenLevel;

                string status = (a.Area > 0.0001 && a.Location != null) ? "placed" : "not_enclosed";

                if (status == "placed") placedCount++;
                else notEnclosedCount++;

                // Apply filters
                if (!string.IsNullOrEmpty(areaSchemeFilter))
                {
                    if (scheme == null || !string.Equals(scheme.Name, areaSchemeFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (!string.IsNullOrEmpty(levelFilter))
                {
                    if (level == null || !string.Equals(level.Name, levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (statusFilter != "all" && !string.Equals(status, statusFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                totalMatching++;
                if (areasList.Count >= limit)
                    continue;

                object locData = null;
                if (a.Location is LocationPoint locPoint)
                {
                    var pt = locPoint.Point;
                    locData = new { x_mm = Math.Round(pt.X * 304.8, 2), y_mm = Math.Round(pt.Y * 304.8, 2), z_mm = Math.Round(pt.Z * 304.8, 2) };
                }

                areasList.Add(new
                {
                    element_id = RevitCompat.GetId(a.Id),
                    unique_id = a.UniqueId,
                    name = a.Name,
                    number = a.Number,
                    status = status,
                    area_m2 = Math.Round(a.Area * 0.09290304, 4),
                    level = level != null ? new { element_id = RevitCompat.GetId(level.Id), name = level.Name } : null,
                    area_scheme = scheme != null ? new { element_id = RevitCompat.GetId(scheme.Id), name = scheme.Name } : null,
                    area_plan = viewPlan != null ? new { element_id = RevitCompat.GetId(viewPlan.Id), name = viewPlan.Name } : null,
                    location = locData
                });
            }

            return CommandResult.Ok(new
            {
                total = totalMatching,
                returned = areasList.Count,
                counts = new { placed = placedCount, not_enclosed = notEnclosedCount },
                areas = areasList
            });
        }
    }
}
