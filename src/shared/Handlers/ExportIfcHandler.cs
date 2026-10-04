using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports the active model to an IFC (Industry Foundation Classes) file using
    /// IFCExportOptions (available Revit 2022+). Revit requires an open transaction
    /// for IFC export; ExportTransaction rolls it back so the model is unchanged.
    ///
    /// The IFCVersion enum gained/renamed members across Revit 2022-2027, so the
    /// FileVersion assignment is wrapped in try/catch and silently falls back to the
    /// IFCExportOptions default when the requested member is unavailable.
    /// </summary>
    public class ExportIfcHandler : IRevitCommand
    {
        public string Name => "export_ifc";

        public string Description =>
            "Export the current model to an IFC (Industry Foundation Classes) file. " +
            "Supports IFC2x3 and IFC4 schemas.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""output_folder"",""file_name""],
  ""properties"":{
    ""output_folder"":{""type"":""string"",""description"":""Absolute folder path. Must exist.""},
    ""file_name"":{""type"":""string"",""description"":""Output .ifc file name (without extension).""},
    ""ifc_version"":{""type"":""string"",""enum"":[""IFC2x3"",""IFC4"",""default""],""default"":""default"",""description"":""IFC schema version.""}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            // IsModifiable only means "a transaction is open"; IsReadOnly is the
            // state no transaction can fix, so refuse that early with a clear reason.
            if (doc.IsReadOnly)
                return CommandResult.Fail("IFC export requires a modifiable document (the current document is read-only).");

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
            if (string.IsNullOrWhiteSpace(outputFolder))
                return CommandResult.Fail("output_folder is required.");

            if (!Path.IsPathRooted(outputFolder))
                return CommandResult.Fail("output_folder must be an absolute rooted path: " + outputFolder);

            if (!Directory.Exists(outputFolder))
                return CommandResult.Fail("output_folder directory does not exist: " + outputFolder);

            var fileName = request.Value<string>("file_name");
            if (string.IsNullOrWhiteSpace(fileName))
                return CommandResult.Fail("file_name is required.");

            // Normalize: callers pass the name without extension; strip a trailing
            // .ifc if supplied so the export does not produce "name.ifc.ifc".
            fileName = fileName.Trim();
            if (fileName.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - 4);

            if (string.IsNullOrWhiteSpace(fileName))
                return CommandResult.Fail("file_name resolves to an empty name after trimming the .ifc extension.");

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return CommandResult.Fail("file_name contains invalid file name characters: " + fileName);

            var ifcVersionRequested = request.Value<string>("ifc_version");
            if (string.IsNullOrWhiteSpace(ifcVersionRequested))
                ifcVersionRequested = "default";
            ifcVersionRequested = ifcVersionRequested.Trim();

            if (!string.Equals(ifcVersionRequested, "IFC2x3", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ifcVersionRequested, "IFC4", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ifcVersionRequested, "default", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail(
                    "ifc_version must be one of: IFC2x3, IFC4, default. Got: " + ifcVersionRequested);
            }

            var opts = new IFCExportOptions();

            // The IFCVersion enum members vary slightly across R22-R27. Attempt the
            // requested assignment; on any failure leave IFCExportOptions at its
            // default and report "default" as the version actually used.
            var ifcVersionUsed = "default";
            try
            {
                if (string.Equals(ifcVersionRequested, "IFC2x3", StringComparison.OrdinalIgnoreCase))
                {
                    opts.FileVersion = IFCVersion.IFC2x3;
                    ifcVersionUsed = "IFC2x3";
                }
                else if (string.Equals(ifcVersionRequested, "IFC4", StringComparison.OrdinalIgnoreCase))
                {
                    opts.FileVersion = IFCVersion.IFC4;
                    ifcVersionUsed = "IFC4";
                }
                // "default" => leave opts.FileVersion untouched.
            }
            catch (Exception)
            {
                // Requested IFCVersion member unavailable in this Revit build —
                // fall back to the IFCExportOptions default.
                opts = new IFCExportOptions();
                ifcVersionUsed = "default";
            }

            var expectedPath = Path.Combine(outputFolder, fileName + ".ifc");

            try
            {
                ExportTransaction.Run(doc, "RvtMcp: export IFC", () => doc.Export(outputFolder, fileName, opts));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("IFC export failed: " + ex.Message);
            }

            // Verify the .ifc file was actually produced.
            if (!File.Exists(expectedPath))
            {
                return CommandResult.Ok(new
                {
                    exported = false,
                    output_path = expectedPath,
                    ifc_version_used = ifcVersionUsed,
                    error = "Export completed without error but the expected .ifc file was not found: " + expectedPath
                });
            }

            long fileSize = 0;
            try
            {
                fileSize = new FileInfo(expectedPath).Length;
            }
            catch
            {
                // Leave fileSize=0 if the file cannot be stat'd (very unlikely after a successful export).
            }

            return CommandResult.Ok(new
            {
                exported = true,
                output_path = expectedPath,
                ifc_version_used = ifcVersionUsed,
                file_size_bytes = fileSize,
                error = (string)null
            });
        }
    }
}
