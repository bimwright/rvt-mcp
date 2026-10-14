using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreatePlaceholderSheetHandler : IRevitCommand
    {
        public string Name => "create_placeholder_sheet";
        public string Description => "Create a new placeholder sheet";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""sheet_number"", ""sheet_name""],
  ""properties"": {
    ""sheet_number"": { ""type"": ""string"" },
    ""sheet_name"": { ""type"": ""string"" }
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

            var sheetNumber = request.Value<string>("sheet_number") ?? request.Value<string>("sheetNumber");
            var sheetName = request.Value<string>("sheet_name") ?? request.Value<string>("sheetName");

            if (string.IsNullOrWhiteSpace(sheetNumber))
                return CommandResult.Fail("sheet_number is required and cannot be empty.");
            if (string.IsNullOrWhiteSpace(sheetName))
                return CommandResult.Fail("sheet_name is required and cannot be empty.");

            // Fail before transaction if sheet number already exists
            var sheetNumberExists = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Any(s => s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));

            if (sheetNumberExists)
                return CommandResult.Fail($"Sheet with number '{sheetNumber}' already exists.");

            using (var tx = new Transaction(doc, "RvtMcp: create placeholder sheet"))
            {
                tx.Start();
                try
                {
                    var newSheet = ViewSheet.CreatePlaceholder(doc);
                    newSheet.SheetNumber = sheetNumber;
                    newSheet.Name = sheetName;

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    return CommandResult.Ok(new
                    {
                        created = true,
                        sheet_id = RevitCompat.GetId(newSheet.Id),
                        sheet_number = newSheet.SheetNumber,
                        sheet_name = newSheet.Name,
                        is_placeholder = newSheet.IsPlaceholder,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Ok(new
                    {
                        created = false,
                        error = ex.Message
                    });
                }
            }
        }
    }
}
