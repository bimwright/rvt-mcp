using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateSheetHandler : IRevitCommand
    {
        public string Name => "create_sheet";
        public string Description => "Create a new sheet with a titleblock";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""sheet_number"", ""sheet_name""],
  ""properties"": {
    ""sheet_number"": { ""type"": ""string"" },
    ""sheet_name"": { ""type"": ""string"" },
    ""title_block_type_id"": { ""type"": ""integer"" },
    ""title_block_name"": { ""type"": ""string"" }
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
            var titleBlockTypeId = request.Value<long?>("title_block_type_id") ?? request.Value<long?>("titleBlockTypeId");
            var titleBlockName = request.Value<string>("title_block_name") ?? request.Value<string>("titleBlockName") ?? "";

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

            // Collect title blocks
            var titleBlockSymbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .ToList();

            FamilySymbol targetSymbol = null;
            if (titleBlockTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(titleBlockTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(titleBlockTypeId.Value));

                targetSymbol = doc.GetElement(RevitCompat.ToElementId(titleBlockTypeId.Value)) as FamilySymbol;
                if (targetSymbol == null)
                    return CommandResult.Fail($"Title block type ID {titleBlockTypeId.Value} not found.");
                if (!IsTitleBlockSymbol(targetSymbol))
                    return CommandResult.Fail($"Element ID {titleBlockTypeId.Value} is not a title block family symbol.");
            }
            else if (!string.IsNullOrEmpty(titleBlockName))
            {
                var matches = titleBlockSymbols.Where(symbol =>
                    symbol.Name.Equals(titleBlockName, StringComparison.OrdinalIgnoreCase) ||
                    $"{symbol.FamilyName}: {symbol.Name}".Equals(titleBlockName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                    return CommandResult.Fail($"Title block '{titleBlockName}' was not found.");
                if (matches.Count > 1)
                    return CommandResult.Fail($"Title block name '{titleBlockName}' is ambiguous. Use title_block_type_id or 'Family: Type'.");

                targetSymbol = matches[0];
            }

            if (targetSymbol == null && titleBlockTypeId == null && string.IsNullOrEmpty(titleBlockName) && titleBlockSymbols.Count > 0)
            {
                targetSymbol = titleBlockSymbols[0];
            }

            var titleBlockId = targetSymbol != null ? targetSymbol.Id : ElementId.InvalidElementId;

            using (var tx = new Transaction(doc, "RvtMcp: create sheet"))
            {
                tx.Start();
                try
                {
                    var newSheet = ViewSheet.Create(doc, titleBlockId);
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
                        title_block_type_id = targetSymbol != null ? RevitCompat.GetId(targetSymbol.Id) : (long?)null,
                        title_block_name = targetSymbol != null ? $"{targetSymbol.FamilyName}: {targetSymbol.Name}" : null,
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

        private static bool IsTitleBlockSymbol(FamilySymbol symbol)
        {
            try
            {
                return symbol != null
                    && symbol.Category != null
                    && RevitCompat.GetId(symbol.Category.Id) == (long)BuiltInCategory.OST_TitleBlocks;
            }
            catch
            {
                return false;
            }
        }
    }
}
