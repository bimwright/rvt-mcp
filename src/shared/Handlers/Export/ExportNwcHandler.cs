using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports the model to a Navisworks NWC file using NavisworksExportOptions
    /// (available Revit 2022+). No Transaction is required for an export operation.
    /// NWC export depends on the Navisworks NWC exporter add-in being installed for
    /// the running Revit version; when it is missing, doc.Export throws and a clean
    /// error DTO is returned instead.
    /// </summary>
    public class ExportNwcHandler : IRevitCommand
    {
        public string Name => "export_nwc";
        public string Description => "Export the model to a Navisworks NWC file. Requires the Navisworks NWC exporter add-in installed for this Revit version.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""output_folder"",""file_name""],
  ""properties"":{
    ""output_folder"":{""type"":""string"",""description"":""Absolute folder path. Must exist.""},
    ""file_name"":{""type"":""string"",""description"":""Output .nwc file name (without extension).""},
    ""export_scope_view_id"":{""type"":""integer"",""description"":""Optional view ElementId to scope the export (uses NavisworksExportScope.View). If omitted, exports the whole model.""}
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

            var fileName = request.Value<string>("file_name");
            if (string.IsNullOrWhiteSpace(fileName))
                return CommandResult.Fail("file_name is required.");

            // Normalize the file name: strip any .nwc extension the caller supplied.
            fileName = fileName.Trim();
            if (fileName.EndsWith(".nwc", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - 4);
            if (string.IsNullOrWhiteSpace(fileName))
                return CommandResult.Fail("file_name resolves to an empty name.");
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return CommandResult.Fail("file_name contains invalid path characters: " + fileName);

            var opts = new NavisworksExportOptions();

            // Optional view scope. When absent, export the whole model.
            var viewIdToken = request["export_scope_view_id"];
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                long rawId;
                try
                {
                    rawId = viewIdToken.Value<long>();
                }
                catch (Exception)
                {
                    return CommandResult.Fail("export_scope_view_id must be an integer. Invalid value: " + viewIdToken);
                }

                if (!RevitCompat.CanRepresentElementId(rawId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(rawId));

                var viewId = RevitCompat.ToElementId(rawId);
                var view = doc.GetElement(viewId) as View;
                if (view == null)
                    return CommandResult.Fail("View with ID " + rawId + " not found.");
                if (view.IsTemplate)
                    return CommandResult.Fail("View with ID " + rawId + " is a view template and cannot be used as an export scope.");

                opts.ExportScope = NavisworksExportScope.View;
                opts.ViewId = viewId;
            }
            else
            {
                opts.ExportScope = NavisworksExportScope.Model;
            }

            var outputPath = Path.Combine(outputFolder, fileName + ".nwc");

            try
            {
                doc.Export(outputFolder, fileName, opts);
            }
            catch (Exception ex)
            {
                // The most common failure here is that the Navisworks NWC exporter
                // add-in is not installed for this Revit version.
                return CommandResult.Ok(new
                {
                    exported = false,
                    output_path = (string)null,
                    error = "NWC export failed — the Navisworks NWC exporter add-in may not be installed for this Revit version. " + ex.Message
                });
            }

            if (!File.Exists(outputPath))
            {
                return CommandResult.Ok(new
                {
                    exported = false,
                    output_path = (string)null,
                    error = "NWC export completed without error but the expected file was not found: " + outputPath
                });
            }

            return CommandResult.Ok(new
            {
                exported = true,
                output_path = outputPath,
                error = (string)null
            });
        }
    }
}
