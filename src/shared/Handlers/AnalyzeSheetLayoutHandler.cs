using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class AnalyzeSheetLayoutHandler : IRevitCommand
    {
        public string Name => "analyze_sheet_layout";
        public string Description => "Analyze a sheet's title block dimensions and viewport positions/scales. Returns title block size and all viewport locations in mm (paper coordinates). Provide sheetNumber or sheetId, or call with no params when active view is a sheet.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""sheetNumber"":{""type"":""string""},""sheetId"":{""type"":""integer""}}}";

        private const double FeetToMm = 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var sheetNumber = request.Value<string>("sheetNumber");
            var sheetId = request.Value<long?>("sheetId");

            ViewSheet sheet = null;

            if (!string.IsNullOrEmpty(sheetNumber))
            {
                sheet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .FirstOrDefault(s => s.SheetNumber.Equals(sheetNumber));

                if (sheet == null)
                    return CommandResult.Fail($"Sheet '{sheetNumber}' not found.");
            }
            else if (sheetId.HasValue)
            {
                sheet = doc.GetElement(RevitCompat.ToElementId(sheetId.Value)) as ViewSheet;
                if (sheet == null)
                    return CommandResult.Fail($"Sheet with ID {sheetId.Value} not found.");
            }
            else
            {
                sheet = doc.ActiveView as ViewSheet;
                if (sheet == null)
                    return CommandResult.Fail("Active view is not a sheet. Provide sheetId or sheetNumber.");
            }

            // Resolve title block
            object titleBlockDto = null;
            var titleBlockElem = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();

            if (titleBlockElem != null)
            {
                var bbox = titleBlockElem.get_BoundingBox(sheet);
                if (bbox != null)
                {
                    var widthMm = (bbox.Max.X - bbox.Min.X) * FeetToMm;
                    var heightMm = (bbox.Max.Y - bbox.Min.Y) * FeetToMm;
                    titleBlockDto = new
                    {
                        typeName = (doc.GetElement(titleBlockElem.GetTypeId()) as ElementType)?.Name,
                        width = widthMm,
                        height = heightMm
                    };
                }
                else
                {
                    var typeElem = doc.GetElement(titleBlockElem.GetTypeId()) as ElementType;
                    titleBlockDto = new
                    {
                        typeName = typeElem?.Name,
                        width = (double?)null,
                        height = (double?)null
                    };
                }
            }

            // Resolve viewports
            var viewportDtos = new List<object>();
            foreach (var vpId in sheet.GetAllViewports())
            {
                var vp = doc.GetElement(vpId) as Viewport;
                if (vp == null) continue;

                var view = doc.GetElement(vp.ViewId) as View;

                // GetBoxCenter returns XYZ; for sheet (paper) coordinates X=U, Y=V
                var center = vp.GetBoxCenter();
                var outline = vp.GetBoxOutline();

                double? uMm = null, vMm = null, widthMm = null, heightMm = null;
                if (center != null)
                {
                    uMm = center.X * FeetToMm;
                    vMm = center.Y * FeetToMm;
                }
                if (outline != null)
                {
                    widthMm = (outline.MaximumPoint.X - outline.MinimumPoint.X) * FeetToMm;
                    heightMm = (outline.MaximumPoint.Y - outline.MinimumPoint.Y) * FeetToMm;
                }

                viewportDtos.Add(new
                {
                    vpId = RevitCompat.GetId(vp.Id),
                    viewId = RevitCompat.GetId(view?.Id),
                    viewName = view?.Name,
                    viewType = view?.ViewType.ToString(),
                    scale = view?.Scale,
                    center = (uMm.HasValue && vMm.HasValue)
                        ? (object)new { u = uMm.Value, v = vMm.Value }
                        : null,
                    width = widthMm,
                    height = heightMm
                });
            }

            return CommandResult.Ok(new
            {
                sheetId = RevitCompat.GetId(sheet.Id),
                sheetNumber = sheet.SheetNumber,
                sheetName = sheet.Name,
                titleBlock = titleBlockDto,
                viewports = viewportDtos
            });
        }
    }
}
