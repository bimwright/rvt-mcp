using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateDetailLineHandler : IRevitCommand
    {
        public string Name => "create_detail_line";
        public string Description => "Create a detail line in a view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""start_x"", ""start_y"", ""end_x"", ""end_y""],
  ""properties"": {
    ""start_x"": { ""type"": ""number"" },
    ""start_y"": { ""type"": ""number"" },
    ""end_x"": { ""type"": ""number"" },
    ""end_y"": { ""type"": ""number"" },
    ""view_id"": { ""type"": ""integer"" },
    ""line_style_id"": { ""type"": ""integer"" }
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

            if (request["start_x"] == null || request["start_y"] == null ||
                request["end_x"] == null || request["end_y"] == null)
            {
                return CommandResult.Fail("start_x, start_y, end_x, and end_y are required parameters.");
            }

            double startX = request.Value<double>("start_x"); // mm
            double startY = request.Value<double>("start_y"); // mm
            double endX = request.Value<double>("end_x"); // mm
            double endY = request.Value<double>("end_y"); // mm
            long? viewId = request.Value<long?>("view_id");
            long? lineStyleId = request.Value<long?>("line_style_id");

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

            // Rejects 3D view for 2D detail line
            if (view.ViewType == ViewType.ThreeD)
                return CommandResult.Fail("Detail lines cannot be created in 3D views.");

            // Coords to feet in view plane
            XYZ startPt, endPt;
            try
            {
                startPt = view.Origin + (view.RightDirection * (startX / 304.8)) + (view.UpDirection * (startY / 304.8));
                endPt = view.Origin + (view.RightDirection * (endX / 304.8)) + (view.UpDirection * (endY / 304.8));
            }
            catch
            {
                startPt = new XYZ(startX / 304.8, startY / 304.8, 0);
                endPt = new XYZ(endX / 304.8, endY / 304.8, 0);
            }

            if (startPt.DistanceTo(endPt) < 0.001)
                return CommandResult.Fail("Detail line must have a non-zero length.");

            DetailCurve detailCurve = null;
            using (var tx = new Transaction(doc, "RvtMcp: create detail line"))
            {
                tx.Start();
                try
                {
                    var line = Line.CreateBound(startPt, endPt);
                    detailCurve = doc.Create.NewDetailCurve(view, line);
                    if (detailCurve == null)
                        throw new Exception("NewDetailCurve returned null.");

                    // Assign line style if supplied
                    if (lineStyleId.HasValue)
                    {
                        if (!RevitCompat.CanRepresentElementId(lineStyleId.Value))
                            throw new Exception("line_style_id " + RevitCompat.ElementIdRangeError(lineStyleId.Value));

                        var gs = doc.GetElement(RevitCompat.ToElementId(lineStyleId.Value)) as GraphicsStyle;
                        if (gs == null)
                            throw new Exception("Line style ID " + lineStyleId.Value + " does not resolve to a GraphicsStyle.");

                        detailCurve.LineStyle = gs;
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Create detail line transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create detail line: " + ex.Message);
                }
            }

            var startFinal = detailCurve.GeometryCurve.GetEndPoint(0);
            var endFinal = detailCurve.GeometryCurve.GetEndPoint(1);
            var lengthMm = detailCurve.GeometryCurve.Length * 304.8;

            return CommandResult.Ok(new JObject
            {
                ["created"] = true,
                ["detail_line_id"] = RevitCompat.GetId(detailCurve.Id),
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["line_style_id"] = RevitCompat.GetId(detailCurve.LineStyle.Id),
                ["start"] = new JObject
                {
                    ["unit"] = "mm",
                    ["x"] = Math.Round(startFinal.X * 304.8, 1),
                    ["y"] = Math.Round(startFinal.Y * 304.8, 1),
                    ["z"] = Math.Round(startFinal.Z * 304.8, 1)
                },
                ["end"] = new JObject
                {
                    ["unit"] = "mm",
                    ["x"] = Math.Round(endFinal.X * 304.8, 1),
                    ["y"] = Math.Round(endFinal.Y * 304.8, 1),
                    ["z"] = Math.Round(endFinal.Z * 304.8, 1)
                },
                ["length"] = Math.Round(lengthMm, 1),
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
