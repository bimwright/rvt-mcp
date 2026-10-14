using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports the active document's energy analytical data to a gbXML
    /// (Green Building XML) file using GBXMLExportOptions.
    /// No Transaction is used (export is a read-only document operation).
    /// gbXML export requires the model to carry energy analytical data — rooms
    /// or spaces with bounding geometry plus energy/analysis settings. When the
    /// model has no analytical data the Revit export throws; that case is caught
    /// and reported as a clean error DTO. Stable on Revit 2022+.
    /// </summary>
    public class ExportGbxmlHandler : IRevitCommand
    {
        public string Name => "export_gbxml";

        public string Description =>
            "Export the model's energy analytical data to gbXML (Green Building XML). " +
            "Provide an absolute output_folder that already exists and a file_name without extension. " +
            "Note: gbXML export requires the model to contain rooms or spaces with energy analytical " +
            "data; a model without rooms/spaces or energy settings cannot be exported.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""output_folder"", ""file_name""],
  ""properties"": {
    ""output_folder"": {""type"": ""string"", ""description"": ""Absolute folder path. Must exist.""},
    ""file_name"": {""type"": ""string"", ""description"": ""Output .xml file name (without extension).""}
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
            var fileName = request.Value<string>("file_name");

            if (string.IsNullOrWhiteSpace(outputFolder))
                return BuildErrorDto(null, "Parameter 'output_folder' is required.");

            if (!Path.IsPathRooted(outputFolder))
                return BuildErrorDto(outputFolder,
                    "output_folder must be an absolute path (e.g. C:\\... or D:\\...). Relative paths are rejected.");

            if (!Directory.Exists(outputFolder))
                return BuildErrorDto(outputFolder, "output_folder does not exist: " + outputFolder);

            if (string.IsNullOrWhiteSpace(fileName))
                return BuildErrorDto(outputFolder, "Parameter 'file_name' is required.");

            // Normalize the file name: strip any directory component and a trailing
            // .xml extension so doc.Export receives a bare name.
            var bareName = fileName.Trim();
            try
            {
                bareName = Path.GetFileName(bareName);
            }
            catch (ArgumentException)
            {
                return BuildErrorDto(outputFolder, "file_name contains invalid path characters: " + fileName);
            }

            if (bareName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                bareName = bareName.Substring(0, bareName.Length - 4);

            if (string.IsNullOrWhiteSpace(bareName))
                return BuildErrorDto(outputFolder, "file_name resolved to an empty name: " + fileName);

            if (bareName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return BuildErrorDto(outputFolder, "file_name contains invalid file name characters: " + fileName);

            var outputPath = Path.Combine(outputFolder, bareName + ".xml");

            // gbXML export. GBXMLExportOptions defaults are sufficient for a basic
            // export; the model must hold energy analytical data for it to succeed.
            try
            {
                var opts = new GBXMLExportOptions();
                doc.Export(outputFolder, bareName, opts);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException revitEx)
            {
                return BuildErrorDto(outputFolder,
                    "gbXML export failed: " + revitEx.Message
                    + " gbXML export requires the model to contain rooms or spaces with energy "
                    + "analytical data and valid energy settings (Manage > Energy Settings).");
            }
            catch (Exception ex)
            {
                return BuildErrorDto(outputFolder,
                    "gbXML export failed: " + ex.Message
                    + " gbXML export requires the model to contain rooms or spaces with energy "
                    + "analytical data and valid energy settings (Manage > Energy Settings).");
            }

            // Confirm the .xml file was actually produced and report its size.
            long fileSizeBytes;
            try
            {
                var info = new FileInfo(outputPath);
                if (!info.Exists)
                    return BuildErrorDto(outputFolder,
                        "gbXML export reported success but no file was found at: " + outputPath
                        + " The model may lack rooms/spaces with energy analytical data.");
                fileSizeBytes = info.Length;
            }
            catch (Exception ex)
            {
                return BuildErrorDto(outputFolder,
                    "gbXML export ran but the output file could not be verified: " + ex.Message);
            }

            return CommandResult.Ok(new
            {
                exported = true,
                output_path = outputPath,
                file_size_bytes = fileSizeBytes,
                error = (string)null
            });
        }

        private static CommandResult BuildErrorDto(string outputFolder, string error)
        {
            return CommandResult.Ok(new
            {
                exported = false,
                output_path = (string)null,
                file_size_bytes = 0L,
                error
            });
        }
    }
}
