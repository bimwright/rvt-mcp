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
    /// Exports sheets or views from the active document to MicroStation DGN files.
    /// No transaction is used (export is a read-only document operation).
    /// DGN export can require a seed file / DGN export settings in some configurations;
    /// when the default export throws, a clean error DTO explains this.
    /// </summary>
    public class ExportDgnHandler : IRevitCommand
    {
        public string Name => "export_dgn";

        public string Description =>
            "Export sheets or views from the active document to MicroStation DGN files. " +
            "Provide an absolute output_folder that already exists. If view_ids is omitted, the active view is exported. " +
            "Note: DGN export may require a DGN seed file / export settings depending on the Revit configuration.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""output_folder""],
  ""properties"": {
    ""output_folder"": {""type"": ""string"", ""description"": ""Absolute folder path. Must exist.""},
    ""view_ids"": {""type"": ""array"", ""items"": {""type"": ""integer""}, ""description"": ""Sheet/view ElementIds. If omitted, active view.""},
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
            var fileNamePrefix = request.Value<string>("file_name_prefix");

            if (string.IsNullOrWhiteSpace(outputFolder))
                return BuildErrorDto(outputFolder, "Parameter 'output_folder' is required.");

            if (!Path.IsPathRooted(outputFolder))
                return BuildErrorDto(outputFolder,
                    "output_folder must be an absolute path (e.g. C:\\... or D:\\...). Relative paths are rejected.");

            if (!Directory.Exists(outputFolder))
                return BuildErrorDto(outputFolder, "output_folder does not exist: " + outputFolder);

            if (!string.IsNullOrWhiteSpace(fileNamePrefix) && !IsSafeFileName(fileNamePrefix))
                return BuildErrorDto(outputFolder, "file_name must be a bare file name with no path separators or '..' segments.");

            // Resolve view ids (active view when omitted).
            var viewIds = new List<ElementId>();
            var viewIdsToken = request["view_ids"];
            if (viewIdsToken != null && viewIdsToken.Type == JTokenType.Array)
            {
                foreach (var token in (JArray)viewIdsToken)
                {
                    long rawId;
                    if (token.Type == JTokenType.Integer)
                    {
                        rawId = token.Value<long>();
                    }
                    else
                    {
                        var asStr = token.Value<string>();
                        if (!long.TryParse((asStr ?? string.Empty).Trim(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out rawId))
                            return BuildErrorDto(outputFolder,
                                "view_ids must contain numeric element ids (got '" + (asStr ?? "<null>") + "').");
                    }

                    if (!RevitCompat.CanRepresentElementId(rawId))
                        return BuildErrorDto(outputFolder, RevitCompat.ElementIdRangeError(rawId));

                    var elId = RevitCompat.ToElementId(rawId);
                    var view = doc.GetElement(elId) as View;
                    if (view == null)
                        return BuildErrorDto(outputFolder,
                            "No View/Sheet element found with id " + rawId.ToString(CultureInfo.InvariantCulture) + ".");

                    if (view.IsTemplate)
                        return BuildErrorDto(outputFolder,
                            "View id " + rawId.ToString(CultureInfo.InvariantCulture)
                            + " is a view template and cannot be exported.");

                    viewIds.Add(view.Id);
                }

                if (viewIds.Count == 0)
                    return BuildErrorDto(outputFolder,
                        "view_ids was provided but empty. Omit it to export the active view.");
            }
            else
            {
                var activeView = doc.ActiveView;
                if (activeView == null)
                    return BuildErrorDto(outputFolder,
                        "No view_ids provided and there is no active view to export.");

                if (activeView.IsTemplate)
                    return BuildErrorDto(outputFolder,
                        "The active view is a view template and cannot be exported.");

                viewIds.Add(activeView.Id);
            }

            var beforeStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, "*.dgn", SearchOption.TopDirectoryOnly))
                    beforeStamps[f] = File.GetLastWriteTimeUtc(f);
            }
            catch { }

            var exportStartUtc = DateTime.UtcNow;

            // DGN export. The default DGNExportOptions + doc.Export works for basic export;
            // some configurations require a seed file / DGN export settings.
            try
            {
                var opts = new DGNExportOptions();
                doc.Export(outputFolder, fileNamePrefix ?? string.Empty, viewIds, opts);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException revitEx)
            {
                return BuildErrorDto(outputFolder,
                    "DGN export failed: " + revitEx.Message
                    + " A DGN seed file or DGN export settings may be required for this configuration.");
            }
            catch (Exception ex)
            {
                return BuildErrorDto(outputFolder,
                    "DGN export failed: " + ex.Message
                    + " A DGN seed file or DGN export settings may be required for this configuration.");
            }

            var files = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, "*.dgn", SearchOption.TopDirectoryOnly))
                {
                    DateTime before;
                    var isNew = !beforeStamps.TryGetValue(f, out before);
                    if (isNew || File.GetLastWriteTimeUtc(f) >= exportStartUtc.AddSeconds(-1))
                        files.Add(f);
                }

                files = files
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                return BuildErrorDto(outputFolder, "Export ran but produced files could not be enumerated: " + ex.Message);
            }

            return CommandResult.Ok(new
            {
                exported = true,
                output_folder = outputFolder,
                file_count = files.Count,
                files,
                note = files.Count == 0 ? "export completed but no output file was detected" : null,
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
                files = Array.Empty<string>(),
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
