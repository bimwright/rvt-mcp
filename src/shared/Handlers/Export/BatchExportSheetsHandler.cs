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
    /// Exports many ViewSheets at once to PDF or DWG via Document.Export.
    /// No transaction is required (export is a read operation).
    /// When sheet_ids is omitted, ALL non-placeholder sheets in the project are
    /// exported, optionally narrowed by a case-insensitive sheet-number substring.
    /// Produced files are identified by diffing the output folder before/after.
    /// </summary>
    public class BatchExportSheetsHandler : IRevitCommand
    {
        public string Name => "batch_export_sheets";

        public string Description =>
            "Export many sheets at once to PDF or DWG. Provide an absolute output_folder that exists " +
            "and a format ('pdf' or 'dwg'). If sheet_ids is omitted, ALL sheets in the project are " +
            "exported; sheet_number_filter optionally narrows that set by a case-insensitive " +
            "substring match on the sheet number.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""output_folder"", ""format""],
  ""properties"": {
    ""output_folder"": {""type"": ""string"", ""description"": ""Absolute folder path. Must exist.""},
    ""format"": {""type"": ""string"", ""enum"": [""pdf"", ""dwg""], ""description"": ""Export format.""},
    ""sheet_ids"": {""type"": ""array"", ""items"": {""type"": ""integer""}, ""description"": ""ViewSheet ElementIds. If omitted, ALL sheets in the project are exported.""},
    ""sheet_number_filter"": {""type"": ""string"", ""description"": ""Optional case-insensitive substring filter on sheet number (applied when sheet_ids omitted).""}
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
            var formatRaw = request.Value<string>("format");
            var sheetNumberFilter = request.Value<string>("sheet_number_filter");

            // Validate output folder.
            if (string.IsNullOrWhiteSpace(outputFolder))
                return BuildErrorDto(null, outputFolder, "Parameter 'output_folder' is required.");

            if (!Path.IsPathRooted(outputFolder))
                return BuildErrorDto(null, outputFolder,
                    "output_folder must be an absolute path (e.g. C:\\... or D:\\...). Relative paths are rejected.");

            if (!Directory.Exists(outputFolder))
                return BuildErrorDto(null, outputFolder, "output_folder does not exist: " + outputFolder);

            // Validate format.
            if (string.IsNullOrWhiteSpace(formatRaw))
                return BuildErrorDto(null, outputFolder, "Parameter 'format' is required ('pdf' or 'dwg').");

            var format = formatRaw.Trim().ToLowerInvariant();
            if (format != "pdf" && format != "dwg")
                return BuildErrorDto(null, outputFolder,
                    "format must be 'pdf' or 'dwg' (got '" + formatRaw + "').");

            var extension = format == "pdf" ? "*.pdf" : "*.dwg";

            // Resolve the sheets to export.
            var sheetIds = new List<ElementId>();
            var idsRawToken = request["sheet_ids"];
            if (idsRawToken != null && idsRawToken.Type != JTokenType.Null)
            {
                var idsToken = idsRawToken as JArray;
                if (idsToken == null)
                    return BuildErrorDto(format, outputFolder, "sheet_ids must be an array of numeric element ids.");

                if (idsToken.Count == 0)
                    return BuildErrorDto(format, outputFolder,
                        "sheet_ids was supplied but empty. Omit it to export all sheets, or provide at least one sheet id.");

                foreach (var token in idsToken)
                {
                    long rawId;
                    try
                    {
                        rawId = token.Value<long>();
                    }
                    catch (Exception)
                    {
                        return BuildErrorDto(format, outputFolder,
                            "sheet_ids must contain numeric element ids (got '" + token + "').");
                    }

                    if (!RevitCompat.CanRepresentElementId(rawId))
                        return BuildErrorDto(format, outputFolder, RevitCompat.ElementIdRangeError(rawId));

                    var elId = RevitCompat.ToElementId(rawId);
                    var element = doc.GetElement(elId);
                    if (element == null)
                        return BuildErrorDto(format, outputFolder,
                            "No element found with id " + rawId.ToString(CultureInfo.InvariantCulture) + ".");

                    var sheet = element as ViewSheet;
                    if (sheet == null)
                        return BuildErrorDto(format, outputFolder,
                            "Element id " + rawId.ToString(CultureInfo.InvariantCulture) + " is not a ViewSheet.");

                    if (sheet.IsPlaceholder)
                        return BuildErrorDto(format, outputFolder,
                            "Sheet id " + rawId.ToString(CultureInfo.InvariantCulture)
                            + " is a placeholder sheet and cannot be exported.");

                    if (sheet.IsTemplate)
                        return BuildErrorDto(format, outputFolder,
                            "Sheet id " + rawId.ToString(CultureInfo.InvariantCulture)
                            + " is a view template and cannot be exported.");

                    if (!sheetIds.Any(existing => existing == sheet.Id))
                        sheetIds.Add(sheet.Id);
                }
            }
            else
            {
                // Collect every non-placeholder sheet in the project.
                var allSheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => s != null && !s.IsPlaceholder && !s.IsTemplate);

                if (!string.IsNullOrWhiteSpace(sheetNumberFilter))
                {
                    var needle = sheetNumberFilter.Trim();
                    allSheets = allSheets.Where(s =>
                        !string.IsNullOrEmpty(s.SheetNumber)
                        && s.SheetNumber.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                foreach (var sheet in allSheets)
                {
                    if (!sheetIds.Any(existing => existing == sheet.Id))
                        sheetIds.Add(sheet.Id);
                }
            }

            if (sheetIds.Count == 0)
                return BuildErrorDto(format, outputFolder,
                    string.IsNullOrWhiteSpace(sheetNumberFilter)
                        ? "No sheets were found to export."
                        : "No sheets matched sheet_number_filter '" + sheetNumberFilter + "'.");

            var beforeStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, extension, SearchOption.TopDirectoryOnly))
                    beforeStamps[f] = File.GetLastWriteTimeUtc(f);
            }
            catch { }

            var exportStartUtc = DateTime.UtcNow;

            // Export.
            try
            {
                if (format == "pdf")
                {
                    var opts = new PDFExportOptions();
                    opts.Combine = false;
                    doc.Export(outputFolder, sheetIds, opts);
                }
                else
                {
                    var opts = new DWGExportOptions();
                    doc.Export(outputFolder, string.Empty, sheetIds, opts);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException revitEx)
            {
                return BuildErrorDto(format, outputFolder,
                    format.ToUpperInvariant() + " export failed: " + revitEx.Message);
            }
            catch (Exception ex)
            {
                return BuildErrorDto(format, outputFolder,
                    format.ToUpperInvariant() + " export failed: " + ex.Message);
            }

            var files = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(outputFolder, extension, SearchOption.TopDirectoryOnly))
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
                return BuildErrorDto(format, outputFolder,
                    "Export completed but output_folder could not be re-read: " + ex.Message);
            }

            return CommandResult.Ok(new
            {
                exported = true,
                format,
                output_folder = outputFolder,
                sheet_count = sheetIds.Count,
                file_count = files.Count,
                files,
                note = files.Count == 0 ? "export completed but no output file was detected" : null,
                error = (string)null
            });
        }

        private static CommandResult BuildErrorDto(string format, string outputFolder, string error)
        {
            return CommandResult.Ok(new
            {
                exported = false,
                format = format ?? string.Empty,
                output_folder = outputFolder ?? string.Empty,
                sheet_count = 0,
                file_count = 0,
                files = new string[0],
                error
            });
        }
    }
}
