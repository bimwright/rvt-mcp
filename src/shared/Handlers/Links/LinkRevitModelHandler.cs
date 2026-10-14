using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class LinkRevitModelHandler : IRevitCommand
    {
        public string Name => "link_revit_model";

        public string Description => "Create a Revit link type and a link instance from a local RVT file.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""path""],
  ""properties"": {
    ""path"": { ""type"": ""string"" },
    ""placement"": { ""type"": ""string"", ""enum"": [""origin"", ""shared""] },
    ""relative"": { ""type"": ""boolean"" },
    ""reuse_existing_type"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            string path = "";
            string placementStr = "origin";
            bool relative = false;
            bool reuseExistingType = false;

            try
            {
                var request = JObject.Parse(paramsJson);
                path = request.Value<string>("path");
                if (request["placement"] != null)
                    placementStr = request.Value<string>("placement") ?? "origin";
                if (request["relative"] != null)
                    relative = request.Value<bool>("relative");
                if (request["reuse_existing_type"] != null)
                    reuseExistingType = request.Value<bool>("reuse_existing_type");
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.Fail("path is required.");

            // Strict Preflight Checks before Transaction
            if (!Path.IsPathRooted(path))
                return CommandResult.Fail($"path must be an absolute rooted path: '{path}'");

            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"path is not a valid absolute path: {ex.Message}");
            }

            if (!File.Exists(canonicalPath))
                return CommandResult.Fail($"File does not exist at path: '{canonicalPath}'");

            string extension = (Path.GetExtension(canonicalPath) ?? string.Empty).ToLowerInvariant();
            if (extension != ".rvt")
                return CommandResult.Fail($"Unsupported file extension '{extension}'. Only .rvt is supported.");

            // Check if active document path
            if (!string.IsNullOrEmpty(doc.PathName) && doc.PathName.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("Cannot link the active document to itself.");

            // Validate placement
            ImportPlacement placementEnum;
            switch (placementStr.ToLowerInvariant())
            {
                case "origin":
                    placementEnum = ImportPlacement.Origin;
                    break;
                case "shared":
                    placementEnum = ImportPlacement.Shared;
                    break;
                default:
                    return CommandResult.Fail($"Invalid placement option '{placementStr}'. Supported: origin, shared");
            }

            var warnings = new List<string>();
            bool reusedType = false;
            ElementId typeId = ElementId.InvalidElementId;
            RevitLinkInstance createdInstance = null;

            using (var tx = new Transaction(doc, "RvtMcp: Link Revit Model"))
            {
                tx.Start();
                try
                {
                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(canonicalPath);

                    if (reuseExistingType)
                    {
                        var existingTypes = new FilteredElementCollector(doc)
                            .OfClass(typeof(RevitLinkType))
                            .Cast<RevitLinkType>();

                        foreach (var type in existingTypes)
                        {
                            var extRef = ExternalFileUtils.GetExternalFileReference(doc, type.Id);
                            if (extRef != null)
                            {
                                var existingPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetPath());
                                if (existingPath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    typeId = type.Id;
                                    reusedType = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (typeId == ElementId.InvalidElementId)
                    {
                        var options = new RevitLinkOptions(relative);
                        var loadResult = RevitLinkType.Create(doc, modelPath, options);
                        typeId = loadResult.ElementId;

                        if (typeId == ElementId.InvalidElementId)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("Failed to create Revit link type. Ensure the file is not corrupted or open in another process.");
                        }
                    }

                    // Create the instance
                    createdInstance = RevitLinkInstance.Create(doc, typeId, placementEnum);

                    if (createdInstance == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("Failed to create Revit link instance.");
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Link Revit model transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to link Revit model: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                created = true,
                reused_existing_type = reusedType,
                link_type_id = RevitCompat.GetId(typeId),
                link_instance_id = RevitCompat.GetId(createdInstance.Id),
                path = canonicalPath,
                placement = placementStr,
                linked_file_status = "Loaded",
                warnings = warnings
            });
        }
    }
}
