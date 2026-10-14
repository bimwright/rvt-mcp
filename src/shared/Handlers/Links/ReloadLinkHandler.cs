using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ReloadLinkHandler : IRevitCommand
    {
        public string Name => "reload_link";

        public string Description => "Reload a Revit link type by type id or instance id. Does not open a transaction.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""link_type_id"": { ""type"": ""integer"" },
    ""link_instance_id"": { ""type"": ""integer"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long? linkTypeId = null;
            long? linkInstanceId = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(paramsJson))
                {
                    var request = JObject.Parse(paramsJson);
                    if (request["link_type_id"] != null)
                        linkTypeId = request.Value<long?>("link_type_id");
                    if (request["link_instance_id"] != null)
                        linkInstanceId = request.Value<long?>("link_instance_id");
                }
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (!linkTypeId.HasValue && !linkInstanceId.HasValue)
                return CommandResult.Fail("Exactly one of link_type_id or link_instance_id must be provided.");
            if (linkTypeId.HasValue && linkInstanceId.HasValue)
                return CommandResult.Fail("Only one of link_type_id or link_instance_id can be provided.");

            RevitLinkType linkType = null;

            if (linkTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(linkTypeId.Value))
                    return CommandResult.Fail("link_type_id " + RevitCompat.ElementIdRangeError(linkTypeId.Value));

                var typeId = RevitCompat.ToElementId(linkTypeId.Value);
                linkType = doc.GetElement(typeId) as RevitLinkType;
                if (linkType == null)
                    return CommandResult.Fail($"Revit link type with ID {linkTypeId.Value} not found.");
            }
            else
            {
                if (!RevitCompat.CanRepresentElementId(linkInstanceId.Value))
                    return CommandResult.Fail("link_instance_id " + RevitCompat.ElementIdRangeError(linkInstanceId.Value));

                var instanceId = RevitCompat.ToElementId(linkInstanceId.Value);
                var instance = doc.GetElement(instanceId) as RevitLinkInstance;
                if (instance == null)
                    return CommandResult.Fail($"Revit link instance with ID {linkInstanceId.Value} not found.");

                linkType = doc.GetElement(instance.GetTypeId()) as RevitLinkType;
                if (linkType == null)
                    return CommandResult.Fail($"Could not retrieve link type for instance ID {linkInstanceId.Value}.");
            }

            long resolvedTypeId = RevitCompat.GetId(linkType.Id);
            bool isLoadedBefore = RevitLinkType.IsLoaded(doc, linkType.Id);
            string statusBefore = isLoadedBefore ? "Loaded" : "Unloaded";

            var warnings = new List<string> { "This operation can clear the undo history." };

            string path = "";
            var extRef = ExternalFileUtils.GetExternalFileReference(doc, linkType.Id);
            if (extRef != null && extRef.GetPath() != null)
            {
                try
                {
                    path = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetPath());
                }
                catch { }
            }

            // CRITICAL: Reload must NOT run inside a transaction
            try
            {
                linkType.Reload();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to reload Revit link '{linkType.Name}' at path '{path}': {ex.Message}");
            }

            bool isLoadedAfter = RevitLinkType.IsLoaded(doc, linkType.Id);

            return CommandResult.Ok(new
            {
                reloaded = isLoadedAfter,
                link_type_id = resolvedTypeId,
                name = linkType.Name,
                status_before = statusBefore,
                status_after = isLoadedAfter ? "Loaded" : "Unloaded",
                path = path,
                warnings = warnings
            });
        }
    }
}
