using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class PublishCoordinatesToLinkHandler : IRevitCommand
    {
        public string Name => "publish_coordinates_to_link";

        public string Description => "Publish host shared coordinates to a linked Revit model.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""link_instance_id""],
  ""properties"": {
    ""link_instance_id"": { ""type"": ""integer"" },
    ""linked_project_location_id"": { ""type"": ""integer"" },
    ""confirm"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long linkInstanceId = 0;
            long? linkedProjectLocationId = null;
            bool confirm = false;

            try
            {
                var request = JObject.Parse(paramsJson);
                linkInstanceId = request.Value<long>("link_instance_id");
                if (request["linked_project_location_id"] != null)
                    linkedProjectLocationId = request.Value<long?>("linked_project_location_id");
                if (request["confirm"] != null)
                    confirm = request.Value<bool>("confirm");
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // STRICT GATE: must fail immediately if confirm = false before starting any transaction
            if (!confirm)
            {
                return CommandResult.Fail("Publishing coordinates requires confirm = true. Warning: this operation will modify the shared coordinate data inside the linked Revit document.");
            }

            if (!RevitCompat.CanRepresentElementId(linkInstanceId))
                return CommandResult.Fail("link_instance_id " + RevitCompat.ElementIdRangeError(linkInstanceId));

            var instanceId = RevitCompat.ToElementId(linkInstanceId);
            var element = doc.GetElement(instanceId);
            if (element == null)
                return CommandResult.Fail($"Element with ID {linkInstanceId} not found.");

            var linkInstance = element as RevitLinkInstance;
            if (linkInstance == null)
            {
                if (element is ImportInstance)
                {
                    return CommandResult.Fail($"Element with ID {linkInstanceId} is a CAD link/import. Revit API PublishCoordinates only supports Revit link instances.");
                }
                return CommandResult.Fail($"Element with ID {linkInstanceId} is not a RevitLinkInstance.");
            }

            var linkDoc = linkInstance.GetLinkDocument();
            if (linkDoc == null)
                return CommandResult.Fail($"The link instance {linkInstanceId} is unloaded or its document is unavailable. Cannot publish coordinates.");

            ElementId linkedLocId = ElementId.InvalidElementId;
            if (linkedProjectLocationId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(linkedProjectLocationId.Value))
                    return CommandResult.Fail("linked_project_location_id " + RevitCompat.ElementIdRangeError(linkedProjectLocationId.Value));

                linkedLocId = RevitCompat.ToElementId(linkedProjectLocationId.Value);
                // Validate if it is a valid project location in the linked document
                var resolvedLoc = linkDoc.GetElement(linkedLocId) as ProjectLocation;
                if (resolvedLoc == null)
                {
                    return CommandResult.Fail($"Project location with ID {linkedProjectLocationId.Value} not found in the linked document.");
                }
            }
            else
            {
                var activeLoc = linkDoc.ActiveProjectLocation;
                if (activeLoc != null)
                {
                    linkedLocId = activeLoc.Id;
                }
                else
                {
                    return CommandResult.Fail("No active project location could be found in the linked document.");
                }
            }

            var warnings = new List<string> { "Publishing coordinates can modify the linked RVT's shared coordinate data." };

            using (var tx = new Transaction(doc, "RvtMcp: Publish Coordinates To Link"))
            {
                tx.Start();
                try
                {
                    var linkElementId = new LinkElementId(linkInstance.Id, linkedLocId);
                    doc.PublishCoordinates(linkElementId);
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Publish coordinates transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Revit API failed to publish coordinates: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                published = true,
                link_instance = new
                {
                    element_id = linkInstanceId,
                    name = linkInstance.Name,
                    type_id = RevitCompat.GetId(linkInstance.GetTypeId())
                },
                linked_project_location_id = RevitCompat.GetId(linkedLocId),
                linked_document_title = linkDoc.Title,
                linked_document_path = linkDoc.PathName,
                warnings = warnings
            });
        }
    }
}
