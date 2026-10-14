using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class PlaceScheduleOnSheetHandler : IRevitCommand
    {
        public string Name => "place_schedule_on_sheet";
        public string Description => "Place a schedule on a sheet using sheet paper coordinates in millimeters";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""x_mm"", ""y_mm""],
  ""properties"": {
    ""sheet_id"": { ""type"": ""integer"" },
    ""sheet_number"": { ""type"": ""string"" },
    ""schedule_id"": { ""type"": ""integer"" },
    ""schedule_name"": { ""type"": ""string"" },
    ""x_mm"": { ""type"": ""number"" },
    ""y_mm"": { ""type"": ""number"" }
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
            catch (Exception ex)
            {
                return CommandResult.Fail($"Parameters must be a JSON object: {ex.Message}");
            }

            var sheetId = request.Value<long?>("sheet_id") ?? request.Value<long?>("sheetId");
            var sheetNumber = request.Value<string>("sheet_number") ?? request.Value<string>("sheetNumber") ?? "";
            var scheduleId = request.Value<long?>("schedule_id") ?? request.Value<long?>("scheduleId");
            var scheduleName = request.Value<string>("schedule_name") ?? request.Value<string>("scheduleName") ?? "";
            var xMm = request.Value<double?>("x_mm") ?? request.Value<double?>("xMm");
            var yMm = request.Value<double?>("y_mm") ?? request.Value<double?>("yMm");

            if (!xMm.HasValue || !yMm.HasValue)
                return CommandResult.Fail("x_mm and y_mm coordinates are required.");

            if (double.IsNaN(xMm.Value) || double.IsInfinity(xMm.Value) || double.IsNaN(yMm.Value) || double.IsInfinity(yMm.Value))
                return CommandResult.Fail("x_mm and y_mm must be finite numbers.");

            // Resolve sheet
            ViewSheet sheet = null;
            if (sheetId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(sheetId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(sheetId.Value));

                sheet = doc.GetElement(RevitCompat.ToElementId(sheetId.Value)) as ViewSheet;
            }
            else if (!string.IsNullOrEmpty(sheetNumber))
            {
                sheet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .FirstOrDefault(s => s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                sheet = doc.ActiveView as ViewSheet;
            }

            if (sheet == null)
                return CommandResult.Fail("Sheet could not be resolved. Provide valid sheet_id, sheet_number, or ensure active view is a sheet.");

            if (sheet.IsPlaceholder)
                return CommandResult.Fail("Cannot place a schedule on a placeholder sheet.");

            // Resolve schedule
            ViewSchedule schedule = null;
            if (scheduleId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(scheduleId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(scheduleId.Value));

                schedule = doc.GetElement(RevitCompat.ToElementId(scheduleId.Value)) as ViewSchedule;
            }
            else if (!string.IsNullOrEmpty(scheduleName))
            {
                schedule = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .FirstOrDefault(vs => vs.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase));
            }

            if (schedule == null)
                return CommandResult.Fail("Schedule could not be resolved. Provide valid schedule_id or schedule_name.");

            // Reject schedule templates, titleblock revision schedules, and internal keynote schedules
            if (schedule.IsTemplate)
                return CommandResult.Fail("Cannot place a schedule template on a sheet.");

            try
            {
                if (schedule.Definition != null)
                {
                    long catId = RevitCompat.GetId(schedule.Definition.CategoryId);
                    if (catId == (long)BuiltInCategory.OST_Revisions)
                        return CommandResult.Fail("Cannot place a titleblock revision schedule directly on a sheet.");
                    if (catId == (long)BuiltInCategory.OST_KeynoteTags)
                        return CommandResult.Fail("Cannot place a keynote legend directly on a sheet.");
                }
            }
            catch { }

            var point = new XYZ(xMm.Value / 304.8, yMm.Value / 304.8, 0);

            using (var tx = new Transaction(doc, "RvtMcp: place schedule on sheet"))
            {
                tx.Start();
                try
                {
                    var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    return CommandResult.Ok(new
                    {
                        placed = true,
                        sheet_id = RevitCompat.GetId(sheet.Id),
                        sheet_number = sheet.SheetNumber,
                        schedule_id = RevitCompat.GetId(schedule.Id),
                        schedule_name = schedule.Name,
                        schedule_instance_id = RevitCompat.GetId(instance.Id),
                        point = new { x_mm = xMm.Value, y_mm = yMm.Value },
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Ok(new
                    {
                        placed = false,
                        error = ex.Message
                    });
                }
            }
        }
    }
}
