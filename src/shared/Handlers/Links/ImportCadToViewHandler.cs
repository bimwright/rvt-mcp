using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ImportCadToViewHandler : IRevitCommand
    {
        public string Name => "import_cad_to_view";

        public string Description => "Import or link a DWG/DXF file into a target view.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""path""],
  ""properties"": {
    ""path"": { ""type"": ""string"" },
    ""view_id"": { ""type"": ""integer"" },
    ""link"": { ""type"": ""boolean"" },
    ""placement"": { ""type"": ""string"", ""enum"": [""origin"", ""center"", ""shared""] },
    ""unit"": { ""type"": ""string"", ""enum"": [""default"", ""millimeter"", ""meter"", ""inch"", ""foot""] },
    ""this_view_only"": { ""type"": ""boolean"" },
    ""visible_layers_only"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            string path = "";
            long? viewId = null;
            bool link = false;
            string placementStr = "origin";
            string unitStr = "default";
            bool thisViewOnly = true;
            bool visibleLayersOnly = true;

            try
            {
                var request = JObject.Parse(paramsJson);
                path = request.Value<string>("path");
                if (request["view_id"] != null)
                    viewId = request.Value<long?>("view_id");
                if (request["link"] != null)
                    link = request.Value<bool>("link");
                if (request["placement"] != null)
                    placementStr = request.Value<string>("placement") ?? "origin";
                if (request["unit"] != null)
                    unitStr = request.Value<string>("unit") ?? "default";
                if (request["this_view_only"] != null)
                    thisViewOnly = request.Value<bool>("this_view_only");
                if (request["visible_layers_only"] != null)
                    visibleLayersOnly = request.Value<bool>("visible_layers_only");
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
            if (extension != ".dwg" && extension != ".dxf")
                return CommandResult.Fail($"Unsupported file extension '{extension}'. Only .dwg and .dxf are supported.");

            // Resolve target view
            View targetView = null;
            if (viewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewId.Value))
                    return CommandResult.Fail("view_id " + RevitCompat.ElementIdRangeError(viewId.Value));

                var viewElement = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
                if (viewElement == null)
                    return CommandResult.Fail($"Target view with ID {viewId.Value} not found.");
                targetView = viewElement;
            }
            else
            {
                targetView = app.ActiveUIDocument?.ActiveView;
                if (targetView == null)
                    return CommandResult.Fail("No active view found and no view_id was supplied.");
            }

            // View compatibility checks
            if (thisViewOnly && targetView.ViewType == ViewType.ThreeD)
                return CommandResult.Fail("Cannot import/link view-specific (this_view_only=true) CAD files into a 3D view.");

            // Setup DWGImportOptions
            var options = new DWGImportOptions
            {
                ThisViewOnly = thisViewOnly,
                VisibleLayersOnly = visibleLayersOnly
            };

            var warnings = new List<string>();

            // Map placement
            switch (placementStr.ToLowerInvariant())
            {
                case "origin":
                    options.Placement = ImportPlacement.Origin;
                    break;
                case "center":
                    options.Placement = ImportPlacement.Centered;
                    break;
                case "shared":
                    options.Placement = ImportPlacement.Shared;
                    // Check if shared coordinates are standard/unconfigured
                    try
                    {
                        var loc = doc.ActiveProjectLocation;
                        if (loc == null)
                            warnings.Add("Importing with shared placement but no active project location is configured.");
                    }
                    catch { }
                    break;
                default:
                    return CommandResult.Fail($"Invalid placement option '{placementStr}'. Supported: origin, center, shared");
            }

            // Map unit
            switch (unitStr.ToLowerInvariant())
            {
                case "default":
                    options.Unit = ImportUnit.Default;
                    break;
                case "millimeter":
                    options.Unit = ImportUnit.Millimeter;
                    break;
                case "meter":
                    options.Unit = ImportUnit.Meter;
                    break;
                case "inch":
                    options.Unit = ImportUnit.Inch;
                    break;
                case "foot":
                    options.Unit = ImportUnit.Foot;
                    break;
                default:
                    return CommandResult.Fail($"Invalid unit option '{unitStr}'. Supported: default, millimeter, meter, inch, foot");
            }

            ElementId createdId = ElementId.InvalidElementId;
            bool importSuccess = false;

            using (var tx = new Transaction(doc, "RvtMcp: Import CAD To View"))
            {
                tx.Start();
                try
                {
                    if (link)
                    {
                        importSuccess = doc.Link(canonicalPath, options, targetView, out createdId);
                    }
                    else
                    {
                        importSuccess = doc.Import(canonicalPath, options, targetView, out createdId);
                    }

                    if (importSuccess && createdId != ElementId.InvalidElementId)
                    {
                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                            return CommandResult.Fail("Import CAD transaction did not commit. Status: " + status);
                    }
                    else
                    {
                        tx.RollBack();
                        return CommandResult.Fail($"Revit did not successfully {(link ? "link" : "import")} the CAD file.");
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to {(link ? "link" : "import")} CAD file: {ex.Message}");
                }
            }

            var createdInstance = doc.GetElement(createdId) as ImportInstance;
            if (createdInstance == null)
                return CommandResult.Fail("CAD import completed, but could not retrieve the created element.");

            return CommandResult.Ok(new
            {
                created = true,
                kind = createdInstance.IsLinked ? "cad_link" : "cad_import",
                instance_id = RevitCompat.GetId(createdInstance.Id),
                type_id = RevitCompat.GetId(createdInstance.GetTypeId()),
                path = canonicalPath,
                view = new
                {
                    element_id = RevitCompat.GetId(targetView.Id),
                    name = targetView.Name,
                    view_type = targetView.ViewType.ToString()
                },
                placement = placementStr,
                this_view_only = thisViewOnly,
                visible_layers_only = visibleLayersOnly,
                linked_file_status = createdInstance.IsLinked ? "Loaded" : "Imported",
                warnings = warnings
            });
        }
    }
}
