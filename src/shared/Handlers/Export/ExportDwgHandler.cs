using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports sheets or views from the current project to AutoCAD DWG files via
    /// Document.Export. No transaction is required (export is a read operation).
    /// If view_ids is omitted, the active view is exported. A saved ExportDWGSettings
    /// may be referenced by name; otherwise default DWGExportOptions are used.
    /// </summary>
    public class ExportDwgHandler : IRevitCommand
    {
        public string Name => "export_dwg";

        public string Description =>
            "Export sheets or views to AutoCAD DWG. Provide an absolute output_folder that exists. " +
            "If view_ids is omitted the active view is exported. settings_name optionally selects a " +
            "saved ExportDWGSettings; otherwise default settings are used.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""output_folder""],
  ""properties"": {
    ""output_folder"": {""type"": ""string"", ""description"": ""Absolute folder path. Must exist.""},
    ""view_ids"": {""type"": ""array"", ""items"": {""type"": ""integer""}, ""description"": ""Sheet/view ElementIds. If omitted, active view.""},
    ""settings_name"": {""type"": ""string"", ""description"": ""Name of a saved ExportDWGSettings. Optional - if omitted, default settings are used.""},
    ""file_name_prefix"": {""type"": ""string"", ""description"": ""Optional prefix for output file names.""}
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

            var outputFolder = request.Value<string>("output_folder");
            var settingsName = request.Value<string>("settings_name");
            var fileNamePrefix = request.Value<string>("file_name_prefix");

            // Validate output folder.
            if (string.IsNullOrWhiteSpace(outputFolder))
                return BuildErrorDto(outputFolder, "Parameter 'output_folder' is required.");

            if (!Path.IsPathRooted(outputFolder))
                return BuildErrorDto(outputFolder,
                    "output_folder must be an absolute path (e.g. C:\\... or D:\\...). Relative paths are rejected.");

            if (!Directory.Exists(outputFolder))
                return BuildErrorDto(outputFolder, "output_folder does not exist: " + outputFolder);

            if (!string.IsNullOrWhiteSpace(fileNamePrefix) && !IsSafeFileName(fileNamePrefix))
                return BuildErrorDto(outputFolder, "file_name must be a bare file name with no path separators or '..' segments.");

            // Resolve view ids (active view if omitted).
            var viewIds = new List<ElementId>();
            var idsToken = request["view_ids"] as JArray;
            if (idsToken != null && idsToken.Count > 0)
            {
                foreach (var token in idsToken)
                {
                    long rawId;
                    try
                    {
                        rawId = token.Value<long>();
                    }
                    catch (Exception)
                    {
                        return BuildErrorDto(outputFolder,
                            "view_ids must contain numeric element ids (got '" + token + "').");
                    }

                    if (!RevitCompat.CanRepresentElementId(rawId))
                        return BuildErrorDto(outputFolder, RevitCompat.ElementIdRangeError(rawId));

                    var elId = RevitCompat.ToElementId(rawId);
                    var element = doc.GetElement(elId);
                    if (element == null)
                        return BuildErrorDto(outputFolder,
                            "No element found with id " + rawId.ToString(CultureInfo.InvariantCulture) + ".");

                    var view = element as View;
                    if (view == null)
                        return BuildErrorDto(outputFolder,
                            "Element id " + rawId.ToString(CultureInfo.InvariantCulture) + " is not a view or sheet.");

                    if (view.IsTemplate)
                        return BuildErrorDto(outputFolder,
                            "View id " + rawId.ToString(CultureInfo.InvariantCulture) + " is a view template and cannot be exported.");

                    if (!viewIds.Any(existing => existing == view.Id))
                        viewIds.Add(view.Id);
                }
            }
            else
            {
                var activeView = doc.ActiveView;
                if (activeView == null || activeView.IsTemplate)
                    return BuildErrorDto(outputFolder,
                        "No view_ids supplied and there is no exportable active view.");
                viewIds.Add(activeView.Id);
            }

            if (viewIds.Count == 0)
                return BuildErrorDto(outputFolder, "No views resolved to export.");

            // Resolve DWG export options (saved settings by name, or defaults).
            DWGExportOptions opts;
            string settingsUsed;
            try
            {
                opts = ResolveExportOptions(doc, settingsName, out settingsUsed);
            }
            catch (Exception ex)
            {
                return BuildErrorDto(outputFolder, ex.Message);
            }

            var beforeStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, "*.dwg", SearchOption.TopDirectoryOnly))
                    beforeStamps[f] = File.GetLastWriteTimeUtc(f);
            }
            catch { }

            var exportStartUtc = DateTime.UtcNow;

            // Export. Overload: Export(string folder, string name, ICollection<ElementId> views, DWGExportOptions options).
            try
            {
                doc.Export(outputFolder, fileNamePrefix ?? string.Empty, viewIds, opts);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException revitEx)
            {
                return BuildErrorDto(outputFolder, "DWG export failed: " + revitEx.Message);
            }
            catch (Exception ex)
            {
                return BuildErrorDto(outputFolder, "DWG export failed: " + ex.Message);
            }

            var produced = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, "*.dwg", SearchOption.TopDirectoryOnly))
                {
                    DateTime before;
                    var isNew = !beforeStamps.TryGetValue(f, out before);
                    if (isNew || File.GetLastWriteTimeUtc(f) >= exportStartUtc.AddSeconds(-1))
                        produced.Add(f);
                }

                produced = produced
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                return BuildErrorDto(outputFolder, "Export completed but output_folder could not be re-read: " + ex.Message);
            }

            return CommandResult.Ok(new
            {
                exported = true,
                output_folder = outputFolder,
                file_count = produced.Count,
                files = produced,
                settings_used = settingsUsed,
                note = produced.Count == 0 ? "export completed but no output file was detected" : null,
                error = (string)null
            });
        }

        /// <summary>
        /// Resolves DWGExportOptions. When settingsName is supplied, looks up a saved
        /// ExportDWGSettings element by name and derives its options; otherwise returns defaults.
        /// </summary>
        private static DWGExportOptions ResolveExportOptions(Document doc, string settingsName, out string settingsUsed)
        {
            if (string.IsNullOrWhiteSpace(settingsName))
            {
                settingsUsed = "<default>";
                return new DWGExportOptions();
            }

            var matches = new FilteredElementCollector(doc)
                .OfClass(typeof(ExportDWGSettings))
                .Cast<ExportDWGSettings>()
                .Where(s => string.Equals(SafeName(s), settingsName, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
            {
                var available = new FilteredElementCollector(doc)
                    .OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>()
                    .Select(SafeName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                var hint = available.Count > 0
                    ? " Available: " + string.Join(", ", available) + "."
                    : " No saved ExportDWGSettings exist in this project.";

                throw new InvalidOperationException(
                    "No saved ExportDWGSettings named '" + settingsName + "' was found." + hint);
            }

            if (matches.Count > 1)
                throw new InvalidOperationException(
                    "Multiple ExportDWGSettings named '" + settingsName + "' found (" + matches.Count
                    + "). Settings names should be unique.");

            var settings = matches[0];
            DWGExportOptions opts = null;
            try
            {
                opts = settings.GetDWGExportOptions();
            }
            catch (Exception)
            {
                opts = null;
            }

            if (opts == null)
                throw new InvalidOperationException(
                    "ExportDWGSettings '" + settingsName + "' did not yield usable export options.");

            settingsUsed = settingsName;
            return opts;
        }

        private static CommandResult BuildErrorDto(string outputFolder, string error)
        {
            return CommandResult.Ok(new
            {
                exported = false,
                output_folder = outputFolder ?? string.Empty,
                file_count = 0,
                files = new string[0],
                settings_used = (string)null,
                error
            });
        }

        private static string SafeName(Element element)
        {
            if (element == null) return null;
            try { return element.Name; }
            catch { return null; }
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
