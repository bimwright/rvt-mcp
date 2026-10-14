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
    public class GetRoomBoundariesHandler : IRevitCommand
    {
        public string Name => "get_room_boundaries";
        public string Description => "Get room boundaries loops and boundary elements";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""required"": [""room_id""],
            ""properties"": {
                ""room_id"": { ""type"": ""integer"" },
                ""boundary_location"": { ""type"": ""string"", ""enum"": [""finish"", ""center"", ""core_center"", ""core_boundary""] },
                ""include_boundary_elements"": { ""type"": ""boolean"" }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long roomId;
            string boundaryLocationStr = "finish";
            bool includeBoundaryElements = true;

            try
            {
                var request = JObject.Parse(paramsJson);
                if (!request.TryGetValue("room_id", out var roomIdVal))
                    return CommandResult.Fail("room_id is required.");

                roomId = roomIdVal.Value<long>();
                if (request.TryGetValue("boundary_location", out var locVal))
                    boundaryLocationStr = locVal.Value<string>() ?? "finish";
                if (request.TryGetValue("include_boundary_elements", out var incVal))
                    includeBoundaryElements = incVal.Value<bool>();
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

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
            var loopsList = new List<object>();

            if (status == "unplaced" || status == "not_enclosed")
            {
                string warnMsg = $"Room is {status}. No boundary loops found.";
                warnings.Add(warnMsg);

                return CommandResult.Ok(new
                {
                    room = new
                    {
                        element_id = roomId,
                        name = room.Name,
                        number = room.Number,
                        status = status
                    },
                    boundary_location = boundaryLocationStr,
                    loop_count = 0,
                    loops = loopsList,
                    warnings = warnings
                });
            }

            var loc = SpatialElementBoundaryLocation.Finish;
            if (string.Equals(boundaryLocationStr, "center", StringComparison.OrdinalIgnoreCase))
                loc = SpatialElementBoundaryLocation.Center;
            else if (string.Equals(boundaryLocationStr, "core_center", StringComparison.OrdinalIgnoreCase))
                loc = SpatialElementBoundaryLocation.CoreCenter;
            else if (string.Equals(boundaryLocationStr, "core_boundary", StringComparison.OrdinalIgnoreCase))
                loc = SpatialElementBoundaryLocation.CoreBoundary;

            var boundaryOptions = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = loc
            };

            IList<IList<BoundarySegment>> loops = null;
            try
            {
                loops = room.GetBoundarySegments(boundaryOptions);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to retrieve boundary segments: {ex.Message}");
            }

            if (loops == null)
            {
                return CommandResult.Ok(new
                {
                    room = new { element_id = roomId, name = room.Name, number = room.Number, status = status },
                    boundary_location = boundaryLocationStr,
                    loop_count = 0,
                    loops = loopsList,
                    warnings = new[] { "SpatialElement.GetBoundarySegments returned null." }
                });
            }

            int loopIdx = 0;
            foreach (var loop in loops)
            {
                var segmentsList = new List<object>();
                double loopLengthM = 0;
                int segIdx = 0;

                foreach (var segment in loop)
                {
                    var curve = segment.GetCurve();
                    if (curve == null) continue;

                    double lengthM = Math.Round(curve.Length * 0.3048, 4);
                    loopLengthM += lengthM;

                    var start = curve.GetEndPoint(0);
                    var end = curve.GetEndPoint(1);

                    long? segElemId = null;
                    long? linkElemIdVal = null;
                    string elemCategory = "";
                    string elemName = "";

                    if (includeBoundaryElements)
                    {
                        var eId = segment.ElementId;
                        if (eId != ElementId.InvalidElementId)
                        {
                            segElemId = RevitCompat.GetId(eId);
                            var boundaryElem = doc.GetElement(eId);
                            if (boundaryElem != null)
                            {
                                elemCategory = boundaryElem.Category?.Name ?? "";
                                elemName = boundaryElem.Name ?? "";
                            }
                        }

                        var linkIdObj = segment.LinkElementId;
                        if (linkIdObj != null && linkIdObj != ElementId.InvalidElementId)
                        {
                            linkElemIdVal = RevitCompat.GetId(linkIdObj);
                        }
                    }

                    string curveType = curve.GetType().Name;
                    List<object> tessellatedPoints = null;
                    if (!string.Equals(curveType, "Line", StringComparison.OrdinalIgnoreCase))
                    {
                        // non-linear curves - extract tessellation
                        var tessPoints = curve.Tessellate();
                        if (tessPoints != null && tessPoints.Count > 0)
                        {
                            tessellatedPoints = tessPoints.Select(p => (object)new
                            {
                                x_mm = Math.Round(p.X * 304.8, 2),
                                y_mm = Math.Round(p.Y * 304.8, 2),
                                z_mm = Math.Round(p.Z * 304.8, 2)
                            }).ToList();
                        }
                    }

                    segmentsList.Add(new
                    {
                        segment_index = segIdx++,
                        curve_type = curveType,
                        start = new { x_mm = Math.Round(start.X * 304.8, 2), y_mm = Math.Round(start.Y * 304.8, 2), z_mm = Math.Round(start.Z * 304.8, 2) },
                        end = new { x_mm = Math.Round(end.X * 304.8, 2), y_mm = Math.Round(end.Y * 304.8, 2), z_mm = Math.Round(end.Z * 304.8, 2) },
                        length_m = lengthM,
                        element_id = segElemId,
                        link_element_id = linkElemIdVal,
                        element_category = elemCategory,
                        element_name = elemName,
                        tessellated_points = tessellatedPoints
                    });
                }

                loopsList.Add(new
                {
                    loop_index = loopIdx++,
                    closed = true,
                    length_m = Math.Round(loopLengthM, 4),
                    segments = segmentsList
                });
            }

            return CommandResult.Ok(new
            {
                room = new { element_id = roomId, name = room.Name, number = room.Number, status = status },
                boundary_location = boundaryLocationStr,
                loop_count = loopsList.Count,
                loops = loopsList,
                warnings = warnings
            });
        }
    }
}
