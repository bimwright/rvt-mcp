using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class PlaceViewOnSheetHandler : IRevitCommand
    {
        public string Name => "place_view_on_sheet";
        public string Description => "Create a sheet and place a view on it, or place a view on an existing sheet";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""sheetId"":{""type"":""integer""},""sheetNumber"":{""type"":""string""},""sheetName"":{""type"":""string""}},""required"":[""viewId""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var viewId = request.Value<long>("viewId");
            var sheetId = request.Value<long?>("sheetId");
            var sheetNumber = request.Value<string>("sheetNumber");
            var sheetName = request.Value<string>("sheetName") ?? "MCP Generated Sheet";

            var view = doc.GetElement(RevitCompat.ToElementId(viewId)) as View;
            if (view == null)
                return CommandResult.Fail($"View with ID {viewId} not found.");

            if (!Viewport.CanAddViewToSheet(doc, sheetId.HasValue ? RevitCompat.ToElementId(sheetId.Value) : ElementId.InvalidElementId, view.Id))
            {
                if (sheetId.HasValue)
                    return CommandResult.Fail("Cannot add this view to the specified sheet (may already be placed).");
            }

            using (var tx = new Transaction(doc, "MCP: Place view on sheet"))
            {
                tx.Start();
                try
                {
                    ViewSheet sheet;

                    if (sheetId.HasValue)
                    {
                        sheet = doc.GetElement(RevitCompat.ToElementId(sheetId.Value)) as ViewSheet;
                        if (sheet == null)
                        {
                            tx.RollBack();
                            return CommandResult.Fail($"Sheet with ID {sheetId.Value} not found.");
                        }
                    }
                    else
                    {
                        // Find a titleblock
                        var titleBlock = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsElementType()
                            .FirstElement() as FamilySymbol;

                        var titleBlockId = titleBlock?.Id ?? ElementId.InvalidElementId;
                        sheet = ViewSheet.Create(doc, titleBlockId);

                        if (!string.IsNullOrEmpty(sheetNumber))
                            sheet.SheetNumber = sheetNumber;
                        sheet.Name = sheetName;
                    }

                    // Place view at center of sheet
                    var center = new XYZ(1.0, 0.75, 0); // approximately center of A3/A4 in feet
                    var viewport = Viewport.Create(doc, sheet.Id, view.Id, center);

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        sheetId = RevitCompat.GetId(sheet.Id),
                        sheetNumber = sheet.SheetNumber,
                        sheetName = sheet.Name,
                        viewportId = RevitCompat.GetId(viewport.Id),
                        viewId = RevitCompat.GetId(view.Id),
                        viewName = view.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed: {ex.Message}");
                }
            }
        }
    }
}
