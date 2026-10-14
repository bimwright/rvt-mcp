using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateCalloutViewHandler : IRevitCommand
    {
        public string Name => "create_callout_view";
        public string Description => "Create a callout view in a parent view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""parent_view_id"", ""min_x"", ""min_y"", ""max_x"", ""max_y""],
  ""properties"": {
    ""parent_view_id"": { ""type"": ""integer"" },
    ""min_x"": { ""type"": ""number"" },
    ""min_y"": { ""type"": ""number"" },
    ""max_x"": { ""type"": ""number"" },
    ""max_y"": { ""type"": ""number"" },
    ""view_family_type_id"": { ""type"": ""integer"" },
    ""name"": { ""type"": ""string"" }
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

            if (request["parent_view_id"] == null ||
                request["min_x"] == null || request["min_y"] == null ||
                request["max_x"] == null || request["max_y"] == null)
            {
                return CommandResult.Fail("parent_view_id, min_x, min_y, max_x, and max_y are required parameters.");
            }

            long parentViewId = request.Value<long>("parent_view_id");
            double minX = request.Value<double>("min_x"); // mm
            double minY = request.Value<double>("min_y"); // mm
            double maxX = request.Value<double>("max_x"); // mm
            double maxY = request.Value<double>("max_y"); // mm
            long? viewFamilyTypeId = request.Value<long?>("view_family_type_id");
            string name = request.Value<string>("name");

            if (maxX <= minX || maxY <= minY)
                return CommandResult.Fail("Invalid crop rectangle: max must be strictly greater than min.");

            if (!RevitCompat.CanRepresentElementId(parentViewId))
                return CommandResult.Fail("parent_view_id " + RevitCompat.ElementIdRangeError(parentViewId));

            // Resolve Parent View
            var parentView = doc.GetElement(RevitCompat.ToElementId(parentViewId)) as View;
            if (parentView == null)
                return CommandResult.Fail("Parent View ID " + parentViewId + " does not resolve to a View.");

            if (parentView.IsTemplate)
                return CommandResult.Fail("Cannot create a callout inside a view template parent.");

            var pvt = parentView.ViewType;
            if (pvt == ViewType.Schedule || pvt == ViewType.ColumnSchedule || pvt == ViewType.ProjectBrowser || pvt == ViewType.SystemBrowser || pvt == ViewType.Legend || pvt == ViewType.DrawingSheet)
            {
                return CommandResult.Fail("Parent view type '" + pvt + "' does not support callout creation.");
            }

            // Resolve ViewFamilyType
            ViewFamilyType calloutType = null;
            if (viewFamilyTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewFamilyTypeId.Value))
                    return CommandResult.Fail("view_family_type_id " + RevitCompat.ElementIdRangeError(viewFamilyTypeId.Value));

                calloutType = doc.GetElement(RevitCompat.ToElementId(viewFamilyTypeId.Value)) as ViewFamilyType;
                if (calloutType == null)
                    return CommandResult.Fail("View family type ID " + viewFamilyTypeId.Value + " does not resolve to a ViewFamilyType.");
            }
            else
            {
                // Detail/Callout compatible types: Detail section, section, or plan
                calloutType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(vt => vt.ViewFamily == ViewFamily.Detail || vt.ViewFamily == ViewFamily.Section || vt.ViewFamily == ViewFamily.FloorPlan);
            }

            if (calloutType == null)
                return CommandResult.Fail("No compatible ViewFamilyType (Detail, Section, or Plan) found in the project.");

            View calloutView = null;
            using (var tx = new Transaction(doc, "RvtMcp: create callout view"))
            {
                tx.Start();
                try
                {
                    // Map points to 3D parent view coordinates
                    XYZ pt1, pt2;
                    try
                    {
                        pt1 = parentView.Origin + (parentView.RightDirection * (minX / 304.8)) + (parentView.UpDirection * (minY / 304.8));
                        pt2 = parentView.Origin + (parentView.RightDirection * (maxX / 304.8)) + (parentView.UpDirection * (maxY / 304.8));
                    }
                    catch
                    {
                        pt1 = new XYZ(minX / 304.8, minY / 304.8, 0);
                        pt2 = new XYZ(maxX / 304.8, maxY / 304.8, 0);
                    }

                    // Create callout
                    calloutView = ViewSection.CreateCallout(doc, parentView.Id, calloutType.Id, pt1, pt2);
                    if (calloutView == null)
                        throw new Exception("ViewSection.CreateCallout returned null.");

                    // Apply name if provided
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        calloutView.Name = name.Trim();
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Create callout view transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create callout view: " + ex.Message);
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["created"] = true,
                ["callout_view_id"] = RevitCompat.GetId(calloutView.Id),
                ["parent_view_id"] = parentViewId,
                ["name"] = calloutView.Name,
                ["view_type"] = calloutView.ViewType.ToString(),
                ["crop"] = new JObject
                {
                    ["unit"] = "mm",
                    ["min"] = new JObject { ["x"] = Math.Round(minX, 1), ["y"] = Math.Round(minY, 1) },
                    ["max"] = new JObject { ["x"] = Math.Round(maxX, 1), ["y"] = Math.Round(maxY, 1) }
                },
                ["error"] = null
            });
        }
    }
}
