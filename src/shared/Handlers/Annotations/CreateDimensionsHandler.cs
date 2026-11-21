using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateDimensionsHandler : IRevitCommand
    {
        public string Name => "create_dimensions";
        public string Description => "Create a dimension element in a view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""references""],
  ""properties"": {
    ""references"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""mode"", ""element_id""],
        ""properties"": {
          ""mode"": { ""type"": ""string"", ""enum"": [""wall_centerline"", ""grid_curve"", ""explicit_reference_stable""] },
          ""element_id"": { ""type"": ""integer"" },
          ""stable_reference"": { ""type"": ""string"" }
        }
      }
    },
    ""view_id"": { ""type"": ""integer"" },
    ""dimension_type_id"": { ""type"": ""integer"" },
    ""line"": {
      ""type"": ""object"",
      ""properties"": {
        ""start"": {
          ""type"": ""object"",
          ""required"": [""x"", ""y"", ""z""],
          ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }
        },
        ""end"": {
          ""type"": ""object"",
          ""required"": [""x"", ""y"", ""z""],
          ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }
        }
      }
    }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var referencesToken = request["references"] as JArray;
            if (referencesToken == null || referencesToken.Count < 2)
                return CommandResult.Fail("At least two references are required to create a dimension.");

            long? viewId = request.Value<long?>("view_id");
            long? dimensionTypeId = request.Value<long?>("dimension_type_id");
            var lineToken = request["line"] as JObject;

            // Resolve View
            View view = null;
            if (viewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewId.Value))
                    return CommandResult.Fail("view_id " + RevitCompat.ElementIdRangeError(viewId.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
                if (view == null)
                    return CommandResult.Fail("View ID " + viewId.Value + " does not resolve to a View.");
            }
            else
            {
                view = doc.ActiveView;
            }

            if (view == null)
                return CommandResult.Fail("No target view could be resolved.");

            if (!EnsureViewCanHostAnnotation(view, out var viewError))
                return CommandResult.Fail(viewError);

            // Resolve DimensionType if supplied
            DimensionType dimType = null;
            if (dimensionTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(dimensionTypeId.Value))
                    return CommandResult.Fail("dimension_type_id " + RevitCompat.ElementIdRangeError(dimensionTypeId.Value));

                dimType = doc.GetElement(RevitCompat.ToElementId(dimensionTypeId.Value)) as DimensionType;
                if (dimType == null)
                    return CommandResult.Fail("Dimension type ID " + dimensionTypeId.Value + " does not resolve to a DimensionType.");
            }

            var refArray = new ReferenceArray();
            var resolvedRefsInfo = new JArray();
            var failedRefsInfo = new JArray();
            var resolvedElements = new List<Element>();
            var resolvedLines = new List<Line>(); // the referenced line in model space, null when unknown

            for (int i = 0; i < referencesToken.Count; i++)
            {
                var refItem = referencesToken[i] as JObject;
                if (refItem == null) continue;

                var mode = refItem.Value<string>("mode");
                var elemId = refItem.Value<long>("element_id");
                var stableRef = refItem.Value<string>("stable_reference");

                if (!RevitCompat.CanRepresentElementId(elemId))
                {
                    failedRefsInfo.Add(new JObject
                    {
                        ["mode"] = mode,
                        ["element_id"] = elemId,
                        ["error"] = RevitCompat.ElementIdRangeError(elemId)
                    });
                    continue;
                }

                var elem = doc.GetElement(RevitCompat.ToElementId(elemId));
                if (elem == null)
                {
                    failedRefsInfo.Add(new JObject
                    {
                        ["mode"] = mode,
                        ["element_id"] = elemId,
                        ["error"] = "Element not found."
                    });
                    continue;
                }

                try
                {
                    Reference revitRef = null;
                    Line refLine = null;
                    if (string.Equals(mode, "explicit_reference_stable", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(stableRef))
                        {
                            throw new Exception("stable_reference string is required for explicit_reference_stable mode.");
                        }
                        revitRef = Reference.ParseFromStableRepresentation(doc, stableRef);
                    }
                    else if (string.Equals(mode, "grid_curve", StringComparison.OrdinalIgnoreCase))
                    {
                        var grid = elem as Grid;
                        if (grid == null)
                        {
                            throw new Exception("Element is not a Grid.");
                        }
                        revitRef = new Reference(grid);
                        refLine = grid.Curve as Line;
                    }
                    else if (string.Equals(mode, "wall_centerline", StringComparison.OrdinalIgnoreCase))
                    {
                        var wall = elem as Wall;
                        if (wall == null)
                        {
                            throw new Exception("Element is not a Wall.");
                        }
                        refLine = GetWallLocationLine(wall, view);
                        revitRef = refLine.Reference;
                    }
                    else
                    {
                        throw new Exception("Unsupported reference mode: '" + mode + "'. Use wall_centerline, grid_curve or explicit_reference_stable (with stable_reference).");
                    }

                    if (revitRef != null)
                    {
                        refArray.Append(revitRef);
                        resolvedElements.Add(elem);
                        resolvedLines.Add(refLine);
                        resolvedRefsInfo.Add(new JObject
                        {
                            ["mode"] = mode,
                            ["element_id"] = elemId,
                            ["resolved"] = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    failedRefsInfo.Add(new JObject
                    {
                        ["mode"] = mode,
                        ["element_id"] = elemId,
                        ["error"] = ex.Message
                    });
                }
            }

            if (refArray.Size < 2)
            {
                const string tooFew = "Fewer than two valid references were successfully resolved. See failed_references.";
                return new CommandResult
                {
                    Success = false,
                    Error = tooFew,
                    Data = new JObject
                    {
                        ["created"] = false,
                        ["resolved_references"] = resolvedRefsInfo,
                        ["failed_references"] = failedRefsInfo,
                        ["error"] = tooFew
                    }
                };
            }

            // Resolve / Infer Line
            Line line = null;
            if (lineToken != null)
            {
                var startToken = lineToken["start"] as JObject;
                var endToken = lineToken["end"] as JObject;
                if (startToken != null && endToken != null)
                {
                    var sx = startToken.Value<double>("x") / 304.8;
                    var sy = startToken.Value<double>("y") / 304.8;
                    var sz = startToken.Value<double>("z") / 304.8;
                    var ex = endToken.Value<double>("x") / 304.8;
                    var ey = endToken.Value<double>("y") / 304.8;
                    var ez = endToken.Value<double>("z") / 304.8;
                    var startPt = new XYZ(sx, sy, sz);
                    var endPt = new XYZ(ex, ey, ez);
                    if (startPt.DistanceTo(endPt) > 0.001)
                    {
                        line = Line.CreateBound(startPt, endPt);
                    }
                }
            }

            // Parallel reference lines in a plan view: dimension across them, perpendicular and at one
            // elevation. Bounding-box centres of walls differ in height and along the wall, which made
            // Revit refuse the inferred direction.
            if (line == null && resolvedLines.Count >= 2 && resolvedLines.All(l => l != null)
                && Math.Abs(view.ViewDirection.DotProduct(XYZ.BasisZ)) > 0.9)
            {
                var first = resolvedLines[0];
                var dir = new XYZ(first.Direction.X, first.Direction.Y, 0);
                if (dir.GetLength() > 1e-9 && resolvedLines.All(l => Math.Abs(new XYZ(l.Direction.X, l.Direction.Y, 0).Normalize().DotProduct(dir.Normalize())) > 0.9999))
                {
                    var across = new XYZ(-dir.Y, dir.X, 0).Normalize();
                    var origin = (first.GetEndPoint(0) + first.GetEndPoint(1)) * 0.5;
                    var offsets = resolvedLines.Select(l => (((l.GetEndPoint(0) + l.GetEndPoint(1)) * 0.5) - origin).DotProduct(across)).ToList();
                    var min = offsets.Min();
                    var max = offsets.Max();
                    if (max - min > 0.001)
                        line = Line.CreateBound(origin + across * min, origin + across * max);
                }
            }

            if (line == null && resolvedElements.Count >= 2)
            {
                try
                {
                    var bbox1 = resolvedElements[0].get_BoundingBox(view) ?? resolvedElements[0].get_BoundingBox(null);
                    var bbox2 = resolvedElements[1].get_BoundingBox(view) ?? resolvedElements[1].get_BoundingBox(null);
                    if (bbox1 != null && bbox2 != null)
                    {
                        var c1 = (bbox1.Min + bbox1.Max) * 0.5;
                        var c2 = (bbox2.Min + bbox2.Max) * 0.5;
                        c2 = new XYZ(c2.X, c2.Y, c1.Z); // one elevation for the dimension line

                        var dir = (c2 - c1);
                        if (dir.GetLength() > 0.001)
                        {
                            var dirNorm = dir.Normalize();
                            var norm = new XYZ(-dirNorm.Y, dirNorm.X, 0); // basic 2D perpendicular
                            if (Math.Abs(view.ViewDirection.DotProduct(XYZ.BasisZ)) < 0.9)
                            {
                                norm = dirNorm.CrossProduct(view.ViewDirection).Normalize();
                            }

                            // Offset by 1 foot (approx 304.8mm)
                            var p1 = c1 + norm * 1.0;
                            var p2 = c2 + norm * 1.0;
                            if (p1.DistanceTo(p2) > 0.001)
                            {
                                line = Line.CreateBound(p1, p2);
                            }
                        }
                    }
                }
                catch { }
            }

            if (line == null)
            {
                return CommandResult.Fail("Could not infer a valid dimension line. Please specify the 'line' parameter.");
            }

            Dimension dimension = null;
            using (var tx = new Transaction(doc, "RvtMcp: create dimensions"))
            {
                tx.Start();
                try
                {
                    dimension = doc.Create.NewDimension(view, line, refArray);
                    if (dimension == null)
                        throw new Exception("NewDimension returned null.");

                    if (dimType != null)
                    {
                        dimension.ChangeTypeId(dimType.Id);
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Create dimensions transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create dimension: " + ex.Message);
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["created"] = true,
                ["dimension_id"] = RevitCompat.GetId(dimension.Id),
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["dimension_type_id"] = RevitCompat.GetId(dimension.GetTypeId()),
                ["reference_count"] = refArray.Size,
                ["line"] = new JObject
                {
                    ["unit"] = "mm",
                    ["start"] = new JObject
                    {
                        ["x"] = Math.Round(line.GetEndPoint(0).X * 304.8, 1),
                        ["y"] = Math.Round(line.GetEndPoint(0).Y * 304.8, 1),
                        ["z"] = Math.Round(line.GetEndPoint(0).Z * 304.8, 1)
                    },
                    ["end"] = new JObject
                    {
                        ["x"] = Math.Round(line.GetEndPoint(1).X * 304.8, 1),
                        ["y"] = Math.Round(line.GetEndPoint(1).Y * 304.8, 1),
                        ["z"] = Math.Round(line.GetEndPoint(1).Z * 304.8, 1)
                    }
                },
                ["resolved_references"] = resolvedRefsInfo,
                ["failed_references"] = failedRefsInfo,
                ["failedCount"] = failedRefsInfo.Count,
                ["error"] = null
            });
        }

        /// <summary>
        /// A LocationCurve's curve carries no Reference, so a wall's location line is read from the
        /// wall geometry (computed references, non-visible objects): the referenced line parallel to
        /// the location line and nearest to it in plan.
        /// </summary>
        private static Line GetWallLocationLine(Wall wall, View view)
        {
            var lc = wall.Location as LocationCurve;
            if (lc == null || lc.Curve == null)
                throw new Exception("Wall location curve is not available.");
            if (!(lc.Curve is Line locationLine))
                throw new Exception("wall_centerline supports straight walls only; this wall's location curve is " + lc.Curve.GetType().Name + ".");

            var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view };
            var geometry = wall.get_Geometry(options);
            var target = Midpoint(locationLine);
            Line best = null;
            var bestDistance = double.MaxValue;
            if (geometry != null)
            {
                foreach (var obj in geometry)
                {
                    if (!(obj is Line candidate) || candidate.Reference == null) continue;
                    if (Math.Abs(candidate.Direction.DotProduct(locationLine.Direction)) < 0.9999) continue;
                    var mid = Midpoint(candidate);
                    var distance = Math.Sqrt((mid.X - target.X) * (mid.X - target.X) + (mid.Y - target.Y) * (mid.Y - target.Y));
                    if (distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }
            }
            if (best == null)
                throw new Exception("Revit did not expose a location-line Reference for this wall in this view.");
            return best;
        }

        private static XYZ Midpoint(Line line) => (line.GetEndPoint(0) + line.GetEndPoint(1)) * 0.5;

        private static bool EnsureViewCanHostAnnotation(View view, out string error)
        {
            error = null;
            if (view == null)
            {
                error = "Target view is null.";
                return false;
            }
            if (view.IsTemplate)
            {
                error = "Cannot add annotations to a view template.";
                return false;
            }
            var vt = view.ViewType;
            if (vt == ViewType.Schedule || vt == ViewType.ColumnSchedule || vt == ViewType.ProjectBrowser || vt == ViewType.SystemBrowser || vt == ViewType.Legend)
            {
                error = "View type '" + vt + "' does not support annotations.";
                return false;
            }
            return true;
        }
    }
}
