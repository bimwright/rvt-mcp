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
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""sheetId"":{""type"":""integer""},""sheetNumber"":{""type"":""string""},""sheetName"":{""type"":""string""},""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""}},""required"":[""viewId""]}";

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
            var xMm = request.Value<double?>("x_mm") ?? request.Value<double?>("xMm");
            var yMm = request.Value<double?>("y_mm") ?? request.Value<double?>("yMm");
            var pointError = SheetPointInput.Validate(xMm, yMm);
            if (pointError != null)
                return CommandResult.Fail(pointError);

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

                    // Requested point, else the title block centre, else the old fixed point (feet).
                    XYZ center;
                    string basis;
                    if (xMm.HasValue)
                    {
                        center = new XYZ(xMm.Value / 304.8, yMm.Value / 304.8, 0);
                        basis = "requested";
                    }
                    else
                    {
                        doc.Regenerate();
                        var titleBlockBox = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType()
                            .FirstElement()?.get_BoundingBox(sheet);
                        if (titleBlockBox != null)
                        {
                            center = new XYZ((titleBlockBox.Min.X + titleBlockBox.Max.X) / 2, (titleBlockBox.Min.Y + titleBlockBox.Max.Y) / 2, 0);
                            basis = "title_block_center";
                        }
                        else
                        {
                            center = new XYZ(1.0, 0.75, 0);
                            basis = "default_point";
                        }
                    }
                    var viewport = Viewport.Create(doc, sheet.Id, view.Id, center);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    var placed = viewport.GetBoxCenter();
                    return CommandResult.Ok(new
                    {
                        sheetId = RevitCompat.GetId(sheet.Id),
                        sheetNumber = sheet.SheetNumber,
                        sheetName = sheet.Name,
                        viewportId = RevitCompat.GetId(viewport.Id),
                        viewId = RevitCompat.GetId(view.Id),
                        viewName = view.Name,
                        center = new { u = Math.Round(placed.X * 304.8, 1), v = Math.Round(placed.Y * 304.8, 1) },
                        placement = basis
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
