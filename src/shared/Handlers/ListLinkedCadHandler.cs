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
    public class ListLinkedCadHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "list_linked_cad";

        public string Description => "List CAD imports and CAD links, preserving the link/import distinction.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""include_imports"": { ""type"": ""boolean"" },
    ""include_links"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            bool includeImports = true;
            bool includeLinks = true;

            try
            {
                if (!string.IsNullOrWhiteSpace(paramsJson))
                {
                    var request = JObject.Parse(paramsJson);
                    if (request["include_imports"] != null)
                        includeImports = request.Value<bool>("include_imports");
                    if (request["include_links"] != null)
                        includeLinks = request.Value<bool>("include_links");
                }
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (!includeImports && !includeLinks)
                return CommandResult.Fail("At least one of include_imports or include_links must be true.");

            var warnings = new List<string>();
            var itemsList = new List<object>();
            int cadLinksCount = 0;
            int cadImportsCount = 0;

            var importsCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            foreach (var instance in importsCollector)
            {
                bool isLinked = instance.IsLinked;

                if (isLinked && !includeLinks)
                    continue;
                if (!isLinked && !includeImports)
                    continue;

                if (isLinked)
                    cadLinksCount++;
                else
                    cadImportsCount++;

                long instanceId = RevitCompat.GetId(instance.Id);
                long typeId = RevitCompat.GetId(instance.GetTypeId());

                string name = "";
                string linkedFileStatus = isLinked ? "Loaded" : "Imported";
                string path = "";
                string absolutePath = "";

                var typeElement = doc.GetElement(instance.GetTypeId());
                if (typeElement != null)
                {
                    name = typeElement.Name;

                    // Resolve path and status from CADLinkType / ExternalFileReference if available.
                    // Imported (non-linked) CAD types are not external file references —
                    // GetExternalFileReference throws ArgumentException for them instead of
                    // returning null, so check IsExternalFileReference first.
                    var extRef = ExternalFileUtils.IsExternalFileReference(doc, typeElement.Id)
                        ? ExternalFileUtils.GetExternalFileReference(doc, typeElement.Id)
                        : null;
                    if (extRef != null)
                    {
                        linkedFileStatus = extRef.GetLinkedFileStatus().ToString();
                        var modelPath = extRef.GetPath();
                        if (modelPath != null)
                        {
                            path = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                            try
                            {
                                if (Path.IsPathRooted(path))
                                {
                                    absolutePath = Path.GetFullPath(path);
                                }
                            }
                            catch
                            {
                                absolutePath = path;
                            }
                        }
                    }
                    else if (isLinked)
                    {
                        linkedFileStatus = "Missing";
                    }
                }
                else
                {
                    name = instance.Name;
                }

                long ownerViewId = 0;
                string ownerViewName = "";
                bool viewSpecific = instance.ViewSpecific;

                if (viewSpecific)
                {
                    ownerViewId = RevitCompat.GetId(instance.OwnerViewId);
                    var view = doc.GetElement(instance.OwnerViewId) as View;
                    if (view != null)
                    {
                        ownerViewName = view.Name;
                    }
                }

                Transform transform = null;
                try
                {
                    transform = instance.GetTransform();
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not read transform for CAD instance {instanceId}: {ex.Message}");
                }

                itemsList.Add(new
                {
                    instance_id = instanceId,
                    type_id = typeId,
                    name = name,
                    is_linked = isLinked,
                    kind = isLinked ? "cad_link" : "cad_import",
                    linked_file_status = linkedFileStatus,
                    path = path,
                    absolute_path = absolutePath,
                    owner_view_id = ownerViewId,
                    owner_view_name = ownerViewName,
                    view_specific = viewSpecific,
                    transform = transform != null ? new
                    {
                        origin = new
                        {
                            x_mm = Math.Round(transform.Origin.X * FeetToMm, 3),
                            y_mm = Math.Round(transform.Origin.Y * FeetToMm, 3),
                            z_mm = Math.Round(transform.Origin.Z * FeetToMm, 3)
                        }
                    } : null
                });
            }

            return CommandResult.Ok(new
            {
                total = itemsList.Count,
                items = itemsList,
                counts = new
                {
                    cad_links = cadLinksCount,
                    cad_imports = cadImportsCount
                },
                warnings = warnings
            });
        }
    }
}
