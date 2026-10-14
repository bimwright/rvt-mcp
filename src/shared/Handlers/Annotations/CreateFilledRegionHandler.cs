using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateFilledRegionHandler : IRevitCommand
    {
        public string Name => "create_filled_region";
        public string Description => "Create a filled region detail item in a view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""points""],
  ""properties"": {
    ""points"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""x"", ""y""],
        ""properties"": {
          ""x"": { ""type"": ""number"" },
          ""y"": { ""type"": ""number"" }
        }
      },
      ""minItems"": 3
    },
    ""view_id"": { ""type"": ""integer"" },
    ""filled_region_type_id"": { ""type"": ""integer"" }
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

            var pointsToken = request["points"] as JArray;
            if (pointsToken == null || pointsToken.Count < 3)
                return CommandResult.Fail("At least 3 points are required to define a filled region.");

            long? viewId = request.Value<long?>("view_id");
            long? filledRegionTypeId = request.Value<long?>("filled_region_type_id");

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

            // Rejects 3D view for 2D detail filled region
            if (view.ViewType == ViewType.ThreeD)
                return CommandResult.Fail("Filled regions cannot be created in 3D views.");

            // Resolve FilledRegionType
            FilledRegionType filledType = null;
            if (filledRegionTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(filledRegionTypeId.Value))
                    return CommandResult.Fail("filled_region_type_id " + RevitCompat.ElementIdRangeError(filledRegionTypeId.Value));

                filledType = doc.GetElement(RevitCompat.ToElementId(filledRegionTypeId.Value)) as FilledRegionType;
                if (filledType == null)
                    return CommandResult.Fail("Filled region type ID " + filledRegionTypeId.Value + " does not resolve to a FilledRegionType.");
            }
            else
            {
                filledType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType))
                    .FirstOrDefault() as FilledRegionType;
            }

            if (filledType == null)
                return CommandResult.Fail("No FilledRegionType found in the project.");

            FilledRegion filledRegion = null;
            double areaM2 = 0;

            using (var tx = new Transaction(doc, "RvtMcp: create filled region"))
            {
                tx.Start();
                try
                {
                    // Map points to 3D in the view plane
                    var pts = new List<XYZ>();
                    foreach (var ptToken in pointsToken)
                    {
                        var px = ptToken.Value<double>("x") / 304.8;
                        var py = ptToken.Value<double>("y") / 304.8;

                        XYZ pt;
                        try
                        {
                            pt = view.Origin + (view.RightDirection * px) + (view.UpDirection * py);
                        }
                        catch
                        {
                            pt = new XYZ(px, py, 0);
                        }
                        pts.Add(pt);
                    }

                    // Remove duplicate adjacent points
                    var uniquePts = new List<XYZ>();
                    foreach (var pt in pts)
                    {
                        if (uniquePts.Count == 0 || uniquePts.Last().DistanceTo(pt) > 0.001)
                        {
                            uniquePts.Add(pt);
                        }
                    }
                    if (uniquePts.Count > 2 && uniquePts.First().DistanceTo(uniquePts.Last()) < 0.001)
                    {
                        uniquePts.RemoveAt(uniquePts.Count - 1);
                    }

                    if (uniquePts.Count < 3)
                    {
                        throw new Exception("Filled region requires at least 3 unique, non-coincident points.");
                    }

                    // Create curves list
                    var curves = new List<Curve>();
                    for (int i = 0; i < uniquePts.Count; i++)
                    {
                        var nextIndex = (i + 1) % uniquePts.Count;
                        curves.Add(Line.CreateBound(uniquePts[i], uniquePts[nextIndex]));
                    }

                    var curveLoop = CurveLoop.Create(curves);
                    var loops = new List<CurveLoop> { curveLoop };

                    filledRegion = FilledRegion.Create(doc, filledType.Id, view.Id, loops);
                    if (filledRegion == null)
                        throw new Exception("FilledRegion.Create returned null.");

                    // Calculate area in m2 (1 sqft = 0.092903 m2)
                    double areaSqFt = 0;
                    var right = view.RightDirection;
                    var up = view.UpDirection;
                    for (int i = 0; i < uniquePts.Count; i++)
                    {
                        var next = uniquePts[(i + 1) % uniquePts.Count];
                        var x1 = uniquePts[i].DotProduct(right);
                        var y1 = uniquePts[i].DotProduct(up);
                        var x2 = next.DotProduct(right);
                        var y2 = next.DotProduct(up);
                        areaSqFt += (x1 * y2 - x2 * y1);
                    }
                    areaSqFt = Math.Abs(areaSqFt) * 0.5;
                    areaM2 = areaSqFt * 0.09290304;

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Create filled region transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create filled region: " + ex.Message);
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["created"] = true,
                ["filled_region_id"] = RevitCompat.GetId(filledRegion.Id),
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["filled_region_type_id"] = RevitCompat.GetId(filledRegion.GetTypeId()),
                ["point_count"] = pointsToken.Count,
                ["area_estimate_m2"] = Math.Round(areaM2, 3),
                ["error"] = null
            });
        }

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
