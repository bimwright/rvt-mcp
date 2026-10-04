using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports sheets or views to Autodesk DWF or DWFx using DWFExportOptions /
    /// DWFXExportOptions (available Revit 2022+). Revit requires a modifiable
    /// document for DWF/DWFx export; ExportTransaction rolls it back afterwards.
    /// </summary>
    public class ExportDwfHandler : IRevitCommand
    {
        public string Name => "export_dwf";
        public string Description => "Export sheets or views to Autodesk DWF/DWFx. Exports the active view when no view_ids are given.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""output_folder""],
  ""properties"":{
    ""output_folder"":{""type"":""string"",""description"":""Absolute folder path. Must exist.""},
    ""view_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Sheet/view ElementIds. If omitted, active view.""},
    ""file_name"":{""type"":""string"",""description"":""Output DWF file name (without extension). Optional.""},
    ""use_dwfx"":{""type"":""boolean"",""default"":false,""description"":""If true, export DWFx instead of DWF.""}
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
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var outputFolder = request.Value<string>("output_folder");
            if (string.IsNullOrWhiteSpace(outputFolder))
                return CommandResult.Fail("output_folder is required.");

            if (!Path.IsPathRooted(outputFolder))
                return CommandResult.Fail("output_folder must be an absolute rooted path: " + outputFolder);

            if (!Directory.Exists(outputFolder))
                return CommandResult.Fail("output_folder directory does not exist: " + outputFolder);

            var useDwfx = request.Value<bool?>("use_dwfx") ?? false;
            var fileName = request.Value<string>("file_name");

            // Resolve view ids; fall back to the active view when none supplied.
            var viewIds = new List<ElementId>();
            var viewIdsToken = request["view_ids"] as JArray;
            if (viewIdsToken != null && viewIdsToken.Count > 0)
            {
                foreach (var token in viewIdsToken)
                {
                    long rawId;
                    try
                    {
                        rawId = token.Value<long>();
                    }
                    catch (Exception)
                    {
                        return CommandResult.Fail("view_ids must contain integers. Invalid value: " + token);
                    }

                    if (!RevitCompat.CanRepresentElementId(rawId))
                        return CommandResult.Fail(RevitCompat.ElementIdRangeError(rawId));

                    var elementId = RevitCompat.ToElementId(rawId);
                    var view = doc.GetElement(elementId) as View;
                    if (view == null)
                        return CommandResult.Fail("View with ID " + rawId + " not found.");
                    if (view.IsTemplate)
                        return CommandResult.Fail("View with ID " + rawId + " is a view template and cannot be exported.");

                    viewIds.Add(elementId);
                }
            }
            else
            {
                var active = doc.ActiveView;
                if (active == null)
                    return CommandResult.Fail("No active view to export and no view_ids were provided.");
                if (active.IsTemplate)
                    return CommandResult.Fail("The active view is a view template and cannot be exported.");
                viewIds.Add(active.Id);
            }

            // DWF (.dwf) and DWFx (.dwfx) are distinct extensions; snapshot the
            // matching extension only so produced files can be identified.
            var extension = useDwfx ? "*.dwfx" : "*.dwf";
            var beforeStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, extension, SearchOption.TopDirectoryOnly))
                    beforeStamps[f] = File.GetLastWriteTimeUtc(f);
            }
            catch { }

            var exportStartUtc = DateTime.UtcNow;

            // A base name is required by the Export overload; derive a sensible
            // default from the document title when the caller omits one.
            var baseName = string.IsNullOrWhiteSpace(fileName)
                ? SanitizeFileName(doc.Title)
                : SanitizeFileName(fileName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "export";

            try
            {
                // The DWF/DWFx Export overloads take a ViewSet, not an ElementId
                // collection (the ElementId-collection overload is SAT/DWG/PDF).
                var viewSet = new ViewSet();
                foreach (var vid in viewIds)
                {
                    var v = doc.GetElement(vid) as View;
                    if (v != null) viewSet.Insert(v);
                }
                if (viewSet.IsEmpty)
                    return CommandResult.Fail("No exportable views resolved for DWF export.");

                // DWFExportOptions and DWFXExportOptions are separate classes with
                // no shared base accepted by doc.Export, so each format gets its
                // own typed Export call.
                if (useDwfx)
                {
                    var dwfxOpts = new DWFXExportOptions();
                    dwfxOpts.ExportObjectData = true;
                    ExportTransaction.Run(doc, "RvtMcp: export DWFx", () => doc.Export(outputFolder, baseName, viewSet, dwfxOpts));
                }
                else
                {
                    var dwfOpts = new DWFExportOptions();
                    dwfOpts.ExportObjectData = true;
                    ExportTransaction.Run(doc, "RvtMcp: export DWF", () => doc.Export(outputFolder, baseName, viewSet, dwfOpts));
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail((useDwfx ? "DWFx" : "DWF") + " export failed: " + ex.Message);
            }

            // Identify files that are new or were overwritten during this export.
            var producedFiles = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, extension, SearchOption.TopDirectoryOnly))
                {
                    DateTime before;
                    var isNew = !beforeStamps.TryGetValue(f, out before);
                    if (isNew || File.GetLastWriteTimeUtc(f) >= exportStartUtc.AddSeconds(-1))
                        producedFiles.Add(f);
                }

                producedFiles = producedFiles
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Export completed but reading output_folder failed: " + ex.Message);
            }

            return CommandResult.Ok(new
            {
                exported = true,
                output_folder = outputFolder,
                file_count = producedFiles.Count,
                files = producedFiles,
                note = producedFiles.Count == 0 ? "export completed but no output file was detected" : null,
                error = (string)null
            });
        }

        /// <summary>Strips characters not permitted in a file name.</summary>
        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Where(c => Array.IndexOf(invalid, c) < 0).ToArray());
            return cleaned.Trim();
        }
    }
}
