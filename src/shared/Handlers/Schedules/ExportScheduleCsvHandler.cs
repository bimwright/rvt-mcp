using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports a ViewSchedule's data to a delimited text/CSV file via
    /// ViewSchedule.Export(folder, name, ViewScheduleExportOptions) — stable Revit 2022+.
    /// No Transaction is required for an export operation.
    /// </summary>
    public class ExportScheduleCsvHandler : IRevitCommand
    {
        public string Name => "export_schedule_csv";
        public string Description => "Export a Revit schedule's data to a delimited text/CSV file. Identify the schedule by schedule_id or schedule_name.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""output_path""],
  ""properties"":{
    ""schedule_id"":{""type"":""integer"",""description"":""ViewSchedule ElementId. Either schedule_id or schedule_name required.""},
    ""schedule_name"":{""type"":""string"",""description"":""ViewSchedule name. Either schedule_id or schedule_name required.""},
    ""output_path"":{""type"":""string"",""description"":""Absolute file path including extension (.csv or .txt).""},
    ""delimiter"":{""type"":""string"",""default"":"","",""description"":""Field delimiter, e.g. ',' or tab.""}
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

            // Validate output_path: absolute, has a file name, parent directory exists.
            var outputPath = request.Value<string>("output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
                return CommandResult.Fail("output_path is required.");

            if (!Path.IsPathRooted(outputPath))
                return CommandResult.Fail("output_path must be an absolute rooted path: " + outputPath);

            string folder;
            string fileName;
            try
            {
                folder = Path.GetDirectoryName(outputPath);
                fileName = Path.GetFileName(outputPath);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("output_path is not a valid file path: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(folder))
                return CommandResult.Fail("output_path must include a parent directory: " + outputPath);

            if (string.IsNullOrWhiteSpace(fileName))
                return CommandResult.Fail("output_path must include a file name: " + outputPath);

            if (!Directory.Exists(folder))
                return CommandResult.Fail("output_path parent directory does not exist: " + folder);

            // Resolve the schedule by id or by name.
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .ToList();

            ViewSchedule schedule = null;
            var scheduleIdToken = request["schedule_id"];
            var scheduleName = request.Value<string>("schedule_name");
            bool hasId = scheduleIdToken != null && scheduleIdToken.Type != JTokenType.Null;

            if (hasId)
            {
                long rawId;
                try
                {
                    rawId = scheduleIdToken.Value<long>();
                }
                catch (Exception)
                {
                    return CommandResult.Fail("schedule_id must be an integer. Invalid value: " + scheduleIdToken);
                }

                if (!RevitCompat.CanRepresentElementId(rawId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(rawId));

                var elementId = RevitCompat.ToElementId(rawId);
                schedule = doc.GetElement(elementId) as ViewSchedule;
                if (schedule == null)
                    return CommandResult.Fail("No ViewSchedule found with ID " + rawId + ".");
            }
            else if (!string.IsNullOrWhiteSpace(scheduleName))
            {
                var matches = schedules
                    .Where(s => string.Equals(s.Name, scheduleName, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count == 0)
                    return CommandResult.Fail("No ViewSchedule found with name '" + scheduleName + "'.");
                if (matches.Count > 1)
                    return CommandResult.Fail("Multiple schedules named '" + scheduleName + "' exist. Use schedule_id instead.");
                schedule = matches[0];
            }
            else
            {
                return CommandResult.Fail("Either schedule_id or schedule_name is required.");
            }

            if (schedule.IsTemplate)
                return CommandResult.Fail("The resolved schedule is a view template and cannot be exported.");

            var delimiter = request.Value<string>("delimiter");
            if (delimiter == null)
                delimiter = ",";

            try
            {
                var opts = new ViewScheduleExportOptions();
                opts.FieldDelimiter = delimiter;
                opts.TextQualifier = ExportTextQualifier.DoubleQuote;

                schedule.Export(folder, fileName, opts);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Schedule export failed: " + ex.Message);
            }

            if (!File.Exists(outputPath))
                return CommandResult.Fail("Export completed but no file was produced at: " + outputPath);

            return CommandResult.Ok(new
            {
                exported = true,
                output_path = outputPath,
                schedule_id = RevitCompat.GetId(schedule.Id),
                schedule_name = schedule.Name,
                error = (string)null
            });
        }
    }
}
