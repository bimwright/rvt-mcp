using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateAreaHandler : IRevitCommand
    {
        public string Name => "create_area";
        public string Description => "Create an area in an existing or newly created area plan";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""required"": [""x"", ""y""],
            ""properties"": {
                ""x"": { ""type"": ""number"" },
                ""y"": { ""type"": ""number"" },
                ""area_plan_view_id"": { ""type"": ""integer"" },
                ""area_plan_view_name"": { ""type"": ""string"" },
                ""area_scheme_name"": { ""type"": ""string"" },
                ""level_name"": { ""type"": ""string"" },
                ""create_area_plan_if_missing"": { ""type"": ""boolean"" },
                ""name"": { ""type"": ""string"" },
                ""number"": { ""type"": ""string"" }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            double x, y;
            long? areaPlanViewId = null;
            string areaPlanViewName = "";
            string areaSchemeName = "";
            string levelName = "";
            bool createAreaPlanIfMissing = false;
            string name = "";
            string number = "";

            try
            {
                var request = JObject.Parse(paramsJson);
                if (!request.TryGetValue("x", out var xVal) || !request.TryGetValue("y", out var yVal))
                    return CommandResult.Fail("x and y are required.");

                x = xVal.Value<double>();
                y = yVal.Value<double>();

                if (request.TryGetValue("area_plan_view_id", out var apvIdVal))
                    areaPlanViewId = apvIdVal.Value<long?>();
                if (request.TryGetValue("area_plan_view_name", out var apvNameVal))
                    areaPlanViewName = apvNameVal.Value<string>() ?? "";
                if (request.TryGetValue("area_scheme_name", out var asNameVal))
                    areaSchemeName = asNameVal.Value<string>() ?? "";
                if (request.TryGetValue("level_name", out var lvVal))
                    levelName = lvVal.Value<string>() ?? "";
                if (request.TryGetValue("create_area_plan_if_missing", out var capVal))
                    createAreaPlanIfMissing = capVal.Value<bool>();
                if (request.TryGetValue("name", out var nVal))
                    name = nVal.Value<string>() ?? "";
                if (request.TryGetValue("number", out var numVal))
                    number = numVal.Value<string>() ?? "";
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            // Resolve scheme
            var allSchemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            AreaScheme scheme = null;
            if (!string.IsNullOrEmpty(areaSchemeName))
            {
                var matching = allSchemes.Where(s => s.Name.Equals(areaSchemeName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matching.Count == 0)
                    return CommandResult.Fail($"Area scheme '{areaSchemeName}' not found. Available schemes: {string.Join(", ", allSchemes.Select(s => s.Name))}");
                if (matching.Count > 1)
                    return CommandResult.Fail($"Ambiguous area scheme name '{areaSchemeName}'. Candidates: {string.Join(", ", matching.Select(m => m.Name))}");
                scheme = matching.First();
            }
            else
            {
                scheme = allSchemes.FirstOrDefault();
                if (scheme == null)
                    return CommandResult.Fail("No area schemes are defined in this document.");
            }

            // Resolve level
            Level level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                var allLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                var matchingLevels = allLevels.Where(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matchingLevels.Count == 0)
                    return CommandResult.Fail($"Level '{levelName}' not found.");
                if (matchingLevels.Count > 1)
                    return CommandResult.Fail($"Ambiguous level name '{levelName}'. Candidates: {string.Join(", ", matchingLevels.Select(m => m.Name))}");
                level = matchingLevels.First();
            }

            // Resolve existing AreaPlan
            ViewPlan areaPlan = null;
            if (areaPlanViewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(areaPlanViewId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(areaPlanViewId.Value));
                areaPlan = doc.GetElement(RevitCompat.ToElementId(areaPlanViewId.Value)) as ViewPlan;
                if (areaPlan == null || areaPlan.ViewType != ViewType.AreaPlan)
                    return CommandResult.Fail($"Specified view ID {areaPlanViewId.Value} is not a valid Area Plan View.");
            }
            else if (!string.IsNullOrEmpty(areaPlanViewName))
            {
                areaPlan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .FirstOrDefault(v => v.ViewType == ViewType.AreaPlan && v.Name.Equals(areaPlanViewName, StringComparison.OrdinalIgnoreCase));
                if (areaPlan == null)
                    return CommandResult.Fail($"Area Plan View named '{areaPlanViewName}' was not found.");
            }
            else
            {
                if (level != null && scheme != null)
                {
                    areaPlan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .FirstOrDefault(v => v.ViewType == ViewType.AreaPlan && v.GenLevel?.Id == level.Id &&
                                             v.AreaScheme?.Id == scheme.Id && !v.IsTemplate);
                }
            }

            bool createdPlan = false;

            if (areaPlan == null)
            {
                if (!createAreaPlanIfMissing)
                {
                    return CommandResult.Fail("No existing Area Plan view was found. Set 'create_area_plan_if_missing' to true and specify a valid level_name and area_scheme_name to create one.");
                }

                if (level == null)
                {
                    // Default to first level
                    level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                    if (level == null)
                        return CommandResult.Fail("No level found in the document to create an area plan.");
                }

                createdPlan = true;
            }

            var tg = new TransactionGroup(doc, "RvtMcp: Create Area");
            if (createdPlan)
            {
                tg.Start();
            }

            try
            {
                using (var tx = new Transaction(doc, "RvtMcp: Create Area Inside"))
                {
                    tx.Start();
                    var failures = new SafeFailuresPreprocessor();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                        .SetClearAfterRollback(true).SetFailuresPreprocessor(failures));

                    if (createdPlan)
                    {
                        areaPlan = ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);
                    }

                    var uvPoint = new UV(x / 304.8, y / 304.8);
                    Area area = null;
                    try
                    {
                        area = doc.Create.NewArea(areaPlan, uvPoint);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Revit was unable to create an area at coordinate ({x}, {y}) in area plan '{areaPlan.Name}': {ex.Message}");
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        var param = area.get_Parameter(BuiltInParameter.ROOM_NAME) ?? area.LookupParameter("Name");
                        if (param != null && !param.IsReadOnly) param.Set(name);
                        else area.Name = name;
                    }

                    if (!string.IsNullOrEmpty(number))
                    {
                        var param = area.get_Parameter(BuiltInParameter.ROOM_NUMBER) ?? area.LookupParameter("Number");
                        if (param != null && !param.IsReadOnly) param.Set(number);
                        else area.Number = number;
                    }

                    var commitStatus = tx.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        if (createdPlan && tg.HasStarted())
                            tg.RollBack();
                        return CommandResult.Fail("Create area transaction did not commit. Status: " + commitStatus
                            + (failures.Messages.Count > 0 ? ". " + string.Join("; ", failures.Messages) : ""));
                    }

                    string status = area.Area > 0.0001 ? "placed" : "not_enclosed";

                    var locPoint = area.Location as LocationPoint;
                    object locData = null;
                    if (locPoint != null)
                    {
                        var pt = locPoint.Point;
                        locData = new { x_mm = Math.Round(pt.X * 304.8, 2), y_mm = Math.Round(pt.Y * 304.8, 2), z_mm = Math.Round(pt.Z * 304.8, 2) };
                    }

                    if (createdPlan)
                    {
                        var groupStatus = tg.Assimilate();
                        if (groupStatus != TransactionStatus.Committed)
                            return CommandResult.Fail("Create area transaction group did not commit. Status: " + groupStatus);
                    }

                    return CommandResult.Ok(new
                    {
                        area = new
                        {
                            element_id = RevitCompat.GetId(area.Id),
                            name = area.Name,
                            number = area.Number,
                            area_m2 = Math.Round(area.Area * 0.09290304, 4),
                            status = status
                        },
                        area_plan = new
                        {
                            element_id = RevitCompat.GetId(areaPlan.Id),
                            name = areaPlan.Name,
                            level_name = areaPlan.GenLevel?.Name ?? "",
                            area_scheme_name = areaPlan.AreaScheme?.Name ?? ""
                        },
                        created_area_plan = createdPlan,
                        location = locData,
                        warnings = failures.HadWarnings ? failures.Messages : null
                    });
                }
            }
            catch (Exception ex)
            {
                if (createdPlan && tg.HasStarted())
                {
                    tg.RollBack();
                }
                return CommandResult.Fail($"Failed to create area: {ex.Message}");
            }
        }
    }
}
