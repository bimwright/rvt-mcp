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
    public class ListLinkedModelsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "list_linked_models";

        public string Description => "List Revit link types and instances, including loaded/unloaded status and external paths.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""include_instances"": { ""type"": ""boolean"" },
    ""include_unloaded"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            bool includeInstances = true;
            bool includeUnloaded = true;

            try
            {
                if (!string.IsNullOrWhiteSpace(paramsJson))
                {
                    var request = JObject.Parse(paramsJson);
                    if (request["include_instances"] != null)
                        includeInstances = request.Value<bool>("include_instances");
                    if (request["include_unloaded"] != null)
                        includeUnloaded = request.Value<bool>("include_unloaded");
                }
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var warnings = new List<string>();

            // Collect all link types
            var linkTypesCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            // Collect all link instances
            var linkInstancesCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            // Group instances by their link type element ID
            var instancesByType = linkInstancesCollector
                .GroupBy(inst => RevitCompat.GetId(inst.GetTypeId()))
                .ToDictionary(g => g.Key, g => g.ToList());

            var linksList = new List<object>();
            int totalTypes = 0;
            int totalInstances = 0;

            foreach (var type in linkTypesCollector)
            {
                long typeId = RevitCompat.GetId(type.Id);
                bool isLoaded = RevitLinkType.IsLoaded(doc, type.Id);

                if (!isLoaded && !includeUnloaded)
                    continue;

                totalTypes++;

                string linkedFileStatus = "Unknown";
                string path = "";
                string absolutePath = "";
                string pathType = "unknown";
                string attachmentType = "Unknown";
                bool isNested = false;

                try
                {
                    attachmentType = type.AttachmentType.ToString();
                    isNested = type.IsNestedLink;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not read properties for link type {typeId}: {ex.Message}");
                }

                var extRef = ExternalFileUtils.GetExternalFileReference(doc, type.Id);
                if (extRef != null)
                {
                    linkedFileStatus = extRef.GetLinkedFileStatus().ToString();
                    var modelPath = extRef.GetPath();
                    if (modelPath != null)
                    {
                        path = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                        if (modelPath.ServerPath)
                        {
                            pathType = "server";
                        }
                        else
                        {
                            try
                            {
                                pathType = extRef.PathType.ToString().ToLowerInvariant();
                            }
                            catch
                            {
                                pathType = "unknown";
                            }
                        }

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
                else
                {
                    linkedFileStatus = isLoaded ? "Loaded" : "Unloaded";
                    warnings.Add($"No external file reference found for link type {type.Name} ({typeId}).");
                }

                var instanceDTOs = new List<object>();
                if (includeInstances && instancesByType.TryGetValue(typeId, out var instances))
                {
                    foreach (var inst in instances)
                    {
                        totalInstances++;
                        Transform transform = null;
                        try
                        {
                            transform = inst.GetTotalTransform();
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Could not read transform for link instance {RevitCompat.GetId(inst.Id)}: {ex.Message}");
                        }

                        instanceDTOs.Add(new
                        {
                            instance_id = RevitCompat.GetId(inst.Id),
                            name = inst.Name,
                            is_loaded = isLoaded,
                            transform = ConvertTransform(transform)
                        });
                    }
                }

                linksList.Add(new
                {
                    link_type_id = typeId,
                    name = type.Name,
                    is_loaded = isLoaded,
                    linked_file_status = linkedFileStatus,
                    path = path,
                    absolute_path = absolutePath,
                    path_type = pathType,
                    is_nested = isNested,
                    attachment_type = attachmentType,
                    instance_count = instanceDTOs.Count,
                    instances = instanceDTOs
                });
            }

            return CommandResult.Ok(new
            {
                total_types = totalTypes,
                total_instances = totalInstances,
                links = linksList,
                warnings = warnings
            });
        }

        private static object ConvertTransform(Transform transform)
        {
            if (transform == null)
                return null;

            return new
            {
                origin = new
                {
                    x_mm = Math.Round(transform.Origin.X * FeetToMm, 3),
                    y_mm = Math.Round(transform.Origin.Y * FeetToMm, 3),
                    z_mm = Math.Round(transform.Origin.Z * FeetToMm, 3)
                },
                basis_x = new { x = Math.Round(transform.BasisX.X, 6), y = Math.Round(transform.BasisX.Y, 6), z = Math.Round(transform.BasisX.Z, 6) },
                basis_y = new { x = Math.Round(transform.BasisY.X, 6), y = Math.Round(transform.BasisY.Y, 6), z = Math.Round(transform.BasisY.Z, 6) },
                basis_z = new { x = Math.Round(transform.BasisZ.X, 6), y = Math.Round(transform.BasisZ.Y, 6), z = Math.Round(transform.BasisZ.Z, 6) }
            };
        }
    }
}
