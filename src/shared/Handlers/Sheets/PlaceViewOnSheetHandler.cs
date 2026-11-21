using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class PlaceViewOnSheetHandler : IRevitCommand
    {
        public string Name => "place_view_on_sheet";
        public string Description => "Create a sheet and place a view on it, or place a view on an existing sheet";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""sheetId"":{""type"":""integer""},""sheetNumber"":{""type"":""string""},""sheetName"":{""type"":""string""},""xMm"":{""type"":""number""},""yMm"":{""type"":""number""}},""required"":[""viewId""]}";

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
            var xMm = request.Value<double?>("xMm");
            var yMm = request.Value<double?>("yMm");
            if (xMm.HasValue != yMm.HasValue)
                return CommandResult.Fail("Pass both xMm and yMm, or neither.");
            if (xMm.HasValue && (double.IsNaN(xMm.Value) || double.IsInfinity(xMm.Value) || double.IsNaN(yMm.Value) || double.IsInfinity(yMm.Value)))
                return CommandResult.Fail("xMm and yMm must be finite numbers.");

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

                    // Without coordinates keep the historical point, roughly the center of an A3/A4 in feet.
                    var center = xMm.HasValue
                        ? new XYZ(xMm.Value / 304.8, yMm.Value / 304.8, 0)
                        : new XYZ(1.0, 0.75, 0);
                    var viewport = Viewport.Create(doc, sheet.Id, view.Id, center);
                    // The viewport outline is computed on regeneration; without one, report the requested point.
                    doc.Regenerate();
                    var placed = viewport.GetBoxCenter() ?? center;

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        sheetId = RevitCompat.GetId(sheet.Id),
                        sheetNumber = sheet.SheetNumber,
                        sheetName = sheet.Name,
                        viewportId = RevitCompat.GetId(viewport.Id),
                        viewId = RevitCompat.GetId(view.Id),
                        viewName = view.Name,
                        center = new { xMm = Math.Round(placed.X * 304.8, 1), yMm = Math.Round(placed.Y * 304.8, 1) }
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
