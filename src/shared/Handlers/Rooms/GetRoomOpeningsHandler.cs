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
    public class GetRoomOpeningsHandler : IRevitCommand
    {
        public string Name => "get_room_openings";
        public string Description => "Get doors and windows associated with a room";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""required"": [""room_id""],
            ""properties"": {
                ""room_id"": { ""type"": ""integer"" },
                ""include_doors"": { ""type"": ""boolean"" },
                ""include_windows"": { ""type"": ""boolean"" }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long roomId;
            bool includeDoors = true;
            bool includeWindows = true;

            try
            {
                var request = JObject.Parse(paramsJson);
                if (!request.TryGetValue("room_id", out var roomIdVal))
                    return CommandResult.Fail("room_id is required.");

                roomId = roomIdVal.Value<long>();
                if (request.TryGetValue("include_doors", out var incDoorsVal))
                    includeDoors = incDoorsVal.Value<bool>();
                if (request.TryGetValue("include_windows", out var incWindowsVal))
                    includeWindows = incWindowsVal.Value<bool>();
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            if (!includeDoors && !includeWindows)
                return CommandResult.Fail("Either include_doors or include_windows must be true.");

            if (!RevitCompat.CanRepresentElementId(roomId))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(roomId));

            var roomElementId = RevitCompat.ToElementId(roomId);
            var room = doc.GetElement(roomElementId) as Room;
            if (room == null)
                return CommandResult.Fail($"Element with ID {roomId} is not a valid Room.");

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

            var warnings = new List<string>();
            var openingsList = new List<object>();
            int doorCount = 0;
            int windowCount = 0;

            if (status == "unplaced")
            {
                warnings.Add("Room is unplaced. Unplaced rooms cannot have associated openings.");
                return CommandResult.Ok(new
                {
                    room = new { element_id = roomId, name = room.Name, number = room.Number },
                    openings = openingsList,
                    counts = new { doors = 0, windows = 0 },
                    warnings = warnings
                });
            }

            var phaseId = room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
            var phase = phaseId == null ? null : doc.GetElement(phaseId) as Phase;
            if (phase == null)
                return CommandResult.Fail("The room has no valid ROOM_PHASE; its openings cannot be resolved reliably.");

            // Collect FamilyInstances of category Doors and Windows
            var categories = new List<BuiltInCategory>();
            if (includeDoors) categories.Add(BuiltInCategory.OST_Doors);
            if (includeWindows) categories.Add(BuiltInCategory.OST_Windows);

            var filter = new ElementMulticategoryFilter(categories);
            var instances = new FilteredElementCollector(doc)
                .WherePasses(filter)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            foreach (var fi in instances)
            {
                bool isAssociated = false;
                var catId = fi.Category?.Id;
                bool isDoor = catId != null && RevitCompat.GetId(catId) == (long)BuiltInCategory.OST_Doors;
                bool isWindow = catId != null && RevitCompat.GetId(catId) == (long)BuiltInCategory.OST_Windows;

                Room fromRoom = null;
                Room toRoom = null;
                Room basicRoom = null;

                if (isDoor)
                {
                    if (phase != null)
                    {
                        try { fromRoom = fi.get_FromRoom(phase); } catch { }
                        try { toRoom = fi.get_ToRoom(phase); } catch { }
                    }
                    else
                    {
                        fromRoom = fi.FromRoom;
                        toRoom = fi.ToRoom;
                    }

                    if ((fromRoom != null && fromRoom.Id == room.Id) || (toRoom != null && toRoom.Id == room.Id))
                    {
                        isAssociated = true;
                    }
                }
                else if (isWindow)
                {
                    if (phase != null)
                    {
                        try { basicRoom = fi.get_Room(phase); } catch { }
                        try { fromRoom = fi.get_FromRoom(phase); } catch { }
                        try { toRoom = fi.get_ToRoom(phase); } catch { }
                    }
                    else
                    {
                        basicRoom = fi.Room;
                        fromRoom = fi.FromRoom;
                        toRoom = fi.ToRoom;
                    }

                    if ((basicRoom != null && basicRoom.Id == room.Id) ||
                        (fromRoom != null && fromRoom.Id == room.Id) ||
                        (toRoom != null && toRoom.Id == room.Id))
                    {
                        isAssociated = true;
                    }
                    else
                    {
                        // Check Bounding Box intersection as fallback
                        var fiBox = fi.get_BoundingBox(null);
                        var roomBox = room.get_BoundingBox(null);
                        if (fiBox != null && roomBox != null)
                        {
                            bool intersects = fiBox.Min.X <= roomBox.Max.X && fiBox.Max.X >= roomBox.Min.X &&
                                               fiBox.Min.Y <= roomBox.Max.Y && fiBox.Max.Y >= roomBox.Min.Y &&
                                               fiBox.Min.Z <= roomBox.Max.Z && fiBox.Max.Z >= roomBox.Min.Z;
                            if (intersects)
                            {
                                isAssociated = true;
                            }
                        }
                    }
                }

                if (isAssociated)
                {
                    if (isDoor) doorCount++;
                    if (isWindow) windowCount++;

                    object locData = null;
                    if (fi.Location is LocationPoint fiLocPoint)
                    {
                        var pt = fiLocPoint.Point;
                        locData = new { x_mm = Math.Round(pt.X * 304.8, 2), y_mm = Math.Round(pt.Y * 304.8, 2), z_mm = Math.Round(pt.Z * 304.8, 2) };
                    }

                    double? widthMm = null;
                    double? heightMm = null;
                    var symbol = fi.Symbol;
                    if (symbol != null)
                    {
                        var wParam = symbol.get_Parameter(BuiltInParameter.GENERIC_WIDTH) ??
                                     symbol.get_Parameter(BuiltInParameter.WINDOW_WIDTH) ??
                                     symbol.LookupParameter("Width");
                        if (wParam != null && wParam.HasValue)
                            widthMm = Math.Round(wParam.AsDouble() * 304.8, 2);

                        var hParam = symbol.get_Parameter(BuiltInParameter.GENERIC_HEIGHT) ??
                                     symbol.get_Parameter(BuiltInParameter.WINDOW_HEIGHT) ??
                                     symbol.LookupParameter("Height");
                        if (hParam != null && hParam.HasValue)
                            heightMm = Math.Round(hParam.AsDouble() * 304.8, 2);
                    }

                    string mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ??
                                  fi.get_Parameter(BuiltInParameter.DOOR_NUMBER)?.AsString() ?? "";

                    openingsList.Add(new
                    {
                        element_id = RevitCompat.GetId(fi.Id),
                        unique_id = fi.UniqueId,
                        category = isDoor ? "Doors" : "Windows",
                        family = symbol?.Family?.Name ?? "",
                        type = symbol?.Name ?? "",
                        mark = mark,
                        from_room_id = fromRoom != null ? (long?)RevitCompat.GetId(fromRoom.Id) : null,
                        to_room_id = toRoom != null ? (long?)RevitCompat.GetId(toRoom.Id) : null,
                        host_id = fi.Host != null ? (long?)RevitCompat.GetId(fi.Host.Id) : null,
                        location = locData,
                        width_mm = widthMm,
                        height_mm = heightMm
                    });
                }
            }

            return CommandResult.Ok(new
            {
                room = new { element_id = roomId, name = room.Name, number = room.Number },
                openings = openingsList,
                counts = new { doors = doorCount, windows = windowCount },
                phase = new { element_id = RevitCompat.GetId(phase.Id), name = phase.Name },
                warnings = warnings
            });
        }
    }
}
