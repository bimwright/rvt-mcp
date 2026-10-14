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
    public class ComputeRoomFinishesHandler : IRevitCommand
    {
        public string Name => "compute_room_finishes";
        public string Description => "Compute and summarize room finish parameters and boundary materials";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""properties"": {
                ""room_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
                ""level_name"": { ""type"": ""string"" },
                ""include_empty"": { ""type"": ""boolean"" },
                ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 20000 },
                ""output"": { ""type"": ""string"", ""enum"": [""inline"", ""file""], ""default"": ""inline"" }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long[] roomIds = null;
            string levelName = "";
            bool includeEmpty = true;
            int limit = 5000;

            try
            {
                if (!string.IsNullOrEmpty(paramsJson))
                {
                    var request = JObject.Parse(paramsJson);
                    if (request.TryGetValue("room_ids", out var idsVal) && idsVal is JArray idsArr)
                    {
                        roomIds = idsArr.Select(token => token.Value<long>()).ToArray();
                    }
                    if (request.TryGetValue("level_name", out var lvVal))
                        levelName = lvVal.Value<string>() ?? "";
                    if (request.TryGetValue("include_empty", out var emptyVal))
                        includeEmpty = emptyVal.Value<bool>();
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

            var roomsToProcess = new List<Room>();

            if (roomIds != null && roomIds.Length > 0)
            {
                var invalidIds = new List<long>();
                var nonRoomIds = new List<long>();

                foreach (var id in roomIds)
                {
                    if (!RevitCompat.CanRepresentElementId(id))
                    {
                        invalidIds.Add(id);
                        continue;
                    }

                    var elemId = RevitCompat.ToElementId(id);
                    var elem = doc.GetElement(elemId);
                    if (elem == null)
                    {
                        invalidIds.Add(id);
                    }
                    else if (!(elem is Room room))
                    {
                        nonRoomIds.Add(id);
                    }
                    else
                    {
                        roomsToProcess.Add(room);
                    }
                }

                if (invalidIds.Count > 0 || nonRoomIds.Count > 0)
                {
                    var errors = new List<string>();
                    if (invalidIds.Count > 0) errors.Add($"Invalid or missing room IDs: {string.Join(", ", invalidIds)}");
                    if (nonRoomIds.Count > 0) errors.Add($"Element IDs that are not Rooms: {string.Join(", ", nonRoomIds)}");
                    return CommandResult.Fail(string.Join("; ", errors));
                }
            }
            else
            {
                var collector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>();

                if (!string.IsNullOrEmpty(levelName))
                {
                    var level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                    if (level != null)
                    {
                        collector = collector.Where(r => r.Level?.Id == level.Id);
                    }
                    else
                    {
                        // Level filter specified but level not found: returns empty list
                        return CommandResult.Ok(new
                        {
                            total = 0,
                            rooms = new List<object>(),
                            summary = new { wall_finishes = new object(), floor_finishes = new object(), ceiling_finishes = new object(), base_finishes = new object() }
                        });
                    }
                }

                roomsToProcess = collector.ToList();
            }

            var processedRooms = new List<object>();
            var wallFinishesSummary = new Dictionary<string, int>();
            var floorFinishesSummary = new Dictionary<string, int>();
            var ceilingFinishesSummary = new Dictionary<string, int>();
            var baseFinishesSummary = new Dictionary<string, int>();

            int count = 0;
            foreach (var room in roomsToProcess)
            {
                if (count >= limit)
                    break;

                string baseFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE)?.AsString() ?? room.LookupParameter("Base Finish")?.AsString() ?? "";
                string floorFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.AsString() ?? room.LookupParameter("Floor Finish")?.AsString() ?? "";
                string ceilingFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.AsString() ?? room.LookupParameter("Ceiling Finish")?.AsString() ?? "";
                string wallFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.AsString() ?? room.LookupParameter("Wall Finish")?.AsString() ?? "";

                bool isEmpty = string.IsNullOrEmpty(baseFinish) && string.IsNullOrEmpty(floorFinish) &&
                               string.IsNullOrEmpty(ceilingFinish) && string.IsNullOrEmpty(wallFinish);

                if (isEmpty && !includeEmpty)
                    continue;

                count++;

                var boundaryMaterials = new List<object>();
                var roomWarnings = new List<string>();

                // Classify room status
                string status = "unplaced";
                var locPoint = room.Location as LocationPoint;
                if (locPoint != null)
                {
                    var options = new SpatialElementBoundaryOptions();
                    var boundary = room.GetBoundarySegments(options);
                    if (room.Area > 0.0001 && boundary != null && boundary.Count > 0)
                    {
                        status = "placed";
                    }
                    else
                    {
                        status = "not_enclosed";
                    }
                }

                if (status == "placed")
                {
                    try
                    {
                        var options = new SpatialElementBoundaryOptions();
                        var loops = room.GetBoundarySegments(options);
                        if (loops != null)
                        {
                            foreach (var loop in loops)
                            {
                                foreach (var segment in loop)
                                {
                                    var eId = segment.ElementId;
                                    if (eId != ElementId.InvalidElementId)
                                    {
                                        var boundaryElem = doc.GetElement(eId);
                                        if (boundaryElem != null)
                                        {
                                            var materials = new List<string>();
                                            try
                                            {
                                                foreach (ElementId matId in boundaryElem.GetMaterialIds(false))
                                                {
                                                    var mat = doc.GetElement(matId) as Material;
                                                    if (mat != null)
                                                    {
                                                        materials.Add(mat.Name);
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                roomWarnings.Add($"Failed to get materials for boundary element {RevitCompat.GetId(eId)}: {ex.Message}");
                                            }

                                            var typeId = boundaryElem.GetTypeId();
                                            var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) as ElementType : null;
                                            string typeName = elemType?.Name ?? boundaryElem.Name ?? "";

                                            var curve = segment.GetCurve();
                                            double lengthM = curve != null ? Math.Round(curve.Length * 0.3048, 4) : 0;

                                            boundaryMaterials.Add(new
                                            {
                                                element_id = RevitCompat.GetId(eId),
                                                category = boundaryElem.Category?.Name ?? "",
                                                type_name = typeName,
                                                material_names = materials,
                                                length_m = lengthM
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        roomWarnings.Add($"Boundary materials extraction failed: {ex.Message}");
                    }
                }

                // Summarize finishes
                IncrementSummary(wallFinishesSummary, wallFinish);
                IncrementSummary(floorFinishesSummary, floorFinish);
                IncrementSummary(ceilingFinishesSummary, ceilingFinish);
                IncrementSummary(baseFinishesSummary, baseFinish);

                processedRooms.Add(new
                {
                    room = new
                    {
                        element_id = RevitCompat.GetId(room.Id),
                        name = room.Name,
                        number = room.Number,
                        level_name = room.Level?.Name ?? ""
                    },
                    finishes = new
                    {
                        base_finish = baseFinish,
                        floor_finish = floorFinish,
                        ceiling_finish = ceilingFinish,
                        wall_finish = wallFinish
                    },
                    boundary_materials = boundaryMaterials,
                    warnings = roomWarnings
                });
            }

            return CommandResult.Ok(new
            {
                total = processedRooms.Count,
                rooms = processedRooms,
                summary = new
                {
                    wall_finishes = wallFinishesSummary,
                    floor_finishes = floorFinishesSummary,
                    ceiling_finishes = ceilingFinishesSummary,
                    base_finishes = baseFinishesSummary
                }
            });
        }

        private void IncrementSummary(Dictionary<string, int> summary, string finish)
        {
            string key = string.IsNullOrEmpty(finish) ? "Empty" : finish;
            if (summary.ContainsKey(key))
            {
                summary[key]++;
            }
            else
            {
                summary[key] = 1;
            }
        }
    }
}
