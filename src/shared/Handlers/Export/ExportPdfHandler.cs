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
    /// Exports sheets or views to PDF using PDFExportOptions (available Revit 2022+).
    /// No Transaction is required for an export operation.
    /// </summary>
    public class ExportPdfHandler : IRevitCommand
    {
        public string Name => "export_pdf";
        public string Description => "Export sheets or views to PDF. Exports the active view when no view_ids are given.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""output_folder""],
  ""properties"":{
    ""output_folder"":{""type"":""string"",""description"":""Absolute folder path. Must exist.""},
    ""view_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Sheet/view ElementIds to export. If omitted, exports the active view.""},
    ""combine"":{""type"":""boolean"",""default"":false,""description"":""If true, combine all into one PDF; else one PDF per view.""},
    ""file_name"":{""type"":""string"",""description"":""Base file name (used for combined PDF, or as prefix). Optional.""}
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

            var combine = request.Value<bool?>("combine") ?? false;
            var fileName = request.Value<string>("file_name");
            if (!string.IsNullOrWhiteSpace(fileName) && !IsSafeFileName(fileName))
                return BuildErrorDto(outputFolder, "file_name must be a bare file name with no path separators or '..' segments.");

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

            var beforeStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, "*.pdf", SearchOption.TopDirectoryOnly))
                    beforeStamps[f] = File.GetLastWriteTimeUtc(f);
            }
            catch { }

            var exportStartUtc = DateTime.UtcNow;

            try
            {
                var opts = new PDFExportOptions();
                opts.Combine = combine;

                if (combine && !string.IsNullOrWhiteSpace(fileName))
                    opts.FileName = fileName;

                doc.Export(outputFolder, viewIds, opts);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("PDF export failed: " + ex.Message);
            }

            // Identify files that are new or were overwritten during this export.
            var producedFiles = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, "*.pdf", SearchOption.TopDirectoryOnly))
                {
                    DateTime before;
                    var isNew = !beforeStamps.TryGetValue(f, out before);
                    if (isNew || File.GetLastWriteTimeUtc(f) >= exportStartUtc.AddSeconds(-1))
                        producedFiles.Add(f);
                }

                if (combine && !string.IsNullOrWhiteSpace(fileName))
                {
                    var expectedCombinedFile = Path.Combine(outputFolder, fileName + ".pdf");
                    if (File.Exists(expectedCombinedFile) &&
                        !producedFiles.Any(f => string.Equals(f, expectedCombinedFile, StringComparison.OrdinalIgnoreCase)))
                        producedFiles.Add(expectedCombinedFile);
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

        private static CommandResult BuildErrorDto(string outputFolder, string error)
        {
            return CommandResult.Ok(new
            {
                exported = false,
                output_folder = outputFolder ?? string.Empty,
                file_count = 0,
                files = new string[0],
                error
            });
        }

        // Returns true if 'name' is a safe bare file name (no path component).
        private static bool IsSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name == "." || name == "..") return false;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;
            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return false;
            if (System.IO.Path.IsPathRooted(name)) return false;
            if (!string.Equals(System.IO.Path.GetFileName(name), name, StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
