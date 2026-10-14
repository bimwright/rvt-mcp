using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListRoomsHandler : IRevitCommand
    {
        public string Name => "list_rooms";
        public string Description => "List rooms with status classification and optional parameters";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""properties"": {
                ""level_name"": { ""type"": ""string"" },
                ""phase_name"": { ""type"": ""string"" },
                ""status"": { ""type"": ""string"", ""enum"": [""all"", ""placed"", ""unplaced"", ""not_enclosed""] },
                ""include_parameters"": { ""type"": ""boolean"" },
                ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 20000 }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            string levelFilter = "";
            string phaseFilter = "";
            string statusFilter = "all";
            bool includeParameters = false;
            int limit = 5000;

            try
            {
                if (!string.IsNullOrEmpty(paramsJson))
                {
                    var request = JObject.Parse(paramsJson);
                    if (request.TryGetValue("level_name", out var lvNameVal))
                        levelFilter = lvNameVal.Value<string>() ?? "";
                    if (request.TryGetValue("phase_name", out var phNameVal))
                        phaseFilter = phNameVal.Value<string>() ?? "";
                    if (request.TryGetValue("status", out var stVal))
                        statusFilter = stVal.Value<string>() ?? "all";
                    if (request.TryGetValue("include_parameters", out var incParamVal))
                        includeParameters = incParamVal.Value<bool>();
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

            var allRooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .ToList();

            var roomsList = new List<object>();
            int placedCount = 0;
            int unplacedCount = 0;
            int notEnclosedCount = 0;
            int totalMatching = 0;

            foreach (var r in allRooms)
            {
                // Classify room status
                string status = "unplaced";
                var locPoint = r.Location as LocationPoint;
                if (locPoint != null)
                {
                    var options = new SpatialElementBoundaryOptions();
                    var boundary = r.GetBoundarySegments(options);
                    if (r.Area > 0.0001 && boundary != null && boundary.Count > 0)
                    {
                        status = "placed";
                    }
                    else
                    {
                        status = "not_enclosed";
                    }
                }

                if (status == "placed") placedCount++;
                else if (status == "unplaced") unplacedCount++;
                else if (status == "not_enclosed") notEnclosedCount++;

                // Filters
                if (!string.IsNullOrEmpty(levelFilter) && !string.Equals(r.Level?.Name, levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(phaseFilter))
                {
                    var phase = r.CreatedPhaseId != ElementId.InvalidElementId ? doc.GetElement(r.CreatedPhaseId) as Phase : null;
                    if (phase == null || !string.Equals(phase.Name, phaseFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (statusFilter != "all" && !string.Equals(status, statusFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                totalMatching++;
                if (roomsList.Count >= limit)
                    continue; // Gather count but don't add beyond limit

                // Details
                var levelData = r.Level != null ? new { element_id = RevitCompat.GetId(r.Level.Id), name = r.Level.Name } : null;

                var phaseObj = r.CreatedPhaseId != ElementId.InvalidElementId ? doc.GetElement(r.CreatedPhaseId) as Phase : null;
                var phaseData = phaseObj != null ? new { element_id = RevitCompat.GetId(phaseObj.Id), name = phaseObj.Name } : null;

                object locData = null;
                if (locPoint != null)
                {
                    var pt = locPoint.Point;
                    locData = new { x_mm = Math.Round(pt.X * 304.8, 2), y_mm = Math.Round(pt.Y * 304.8, 2), z_mm = Math.Round(pt.Z * 304.8, 2) };
                }

                var paramDict = new Dictionary<string, object>();
                if (includeParameters)
                {
                    foreach (Parameter param in r.Parameters)
                    {
                        try
                        {
                            if (param.HasValue && !string.IsNullOrEmpty(param.Definition.Name))
                            {
                                switch (param.StorageType)
                                {
                                    case StorageType.Double:
                                        paramDict[param.Definition.Name] = param.AsDouble();
                                        break;
                                    case StorageType.Integer:
                                        paramDict[param.Definition.Name] = param.AsInteger();
                                        break;
                                    case StorageType.String:
                                        paramDict[param.Definition.Name] = param.AsString();
                                        break;
                                    case StorageType.ElementId:
                                        paramDict[param.Definition.Name] = RevitCompat.GetId(param.AsElementId());
                                        break;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore unreadable parameters per spec
                        }
                    }
                }

                var department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "";
                var occupancy = r.get_Parameter(BuiltInParameter.ROOM_OCCUPANCY)?.AsString() ?? "";

                roomsList.Add(new
                {
                    element_id = RevitCompat.GetId(r.Id),
                    unique_id = r.UniqueId,
                    name = r.Name,
                    number = r.Number,
                    level = levelData,
                    phase = phaseData,
                    status = status,
                    location = locData,
                    area_m2 = Math.Round(r.Area * 0.09290304, 4),
                    perimeter_m = Math.Round(r.Perimeter * 0.3048, 4),
                    volume_m3 = Math.Round(r.Volume * 0.0283168, 4),
                    department = department,
                    occupancy = occupancy,
                    parameters = includeParameters ? paramDict : null
                });
            }

            return CommandResult.Ok(new
            {
                total = totalMatching,
                returned = roomsList.Count,
                filters = new { level_name = levelFilter, phase_name = phaseFilter, status = statusFilter },
                counts = new { placed = placedCount, unplaced = unplacedCount, not_enclosed = notEnclosedCount },
                rooms = roomsList
            });
        }
    }
}
