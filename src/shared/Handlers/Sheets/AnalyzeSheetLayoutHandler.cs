using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AnalyzeSheetLayoutHandler : IRevitCommand
    {
        public string Name => "analyze_sheet_layout";
        public string Description => "Analyze a sheet's title block and a bounded page of viewport positions/scales in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""sheetNumber"":{""type"":""string""},""sheetId"":{""type"":""integer""},""start_viewport"":{""type"":""integer"",""default"":0,""minimum"":0},""max_viewports"":{""type"":""integer"",""default"":100,""minimum"":1,""maximum"":500}}}";

        private const double FeetToMm = 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var sheetNumber = request.Value<string>("sheetNumber");
            var sheetId = request.Value<long?>("sheetId");
            if (!ResponsePaging.TryParse(request, "start_viewport", "max_viewports", 100, 500, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);

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
            SheetRect? titleBlockRectMm = null;
            var titleBlockElem = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();

            if (titleBlockElem != null)
            {
                var bbox = titleBlockElem.get_BoundingBox(sheet);
                if (bbox != null)
                {
                    var minUMm = bbox.Min.X * FeetToMm;
                    var minVMm = bbox.Min.Y * FeetToMm;
                    var maxUMm = bbox.Max.X * FeetToMm;
                    var maxVMm = bbox.Max.Y * FeetToMm;
                    titleBlockRectMm = new SheetRect(minUMm, minVMm, maxUMm, maxVMm);
                    titleBlockDto = new
                    {
                        typeName = (doc.GetElement(titleBlockElem.GetTypeId()) as ElementType)?.Name,
                        width = maxUMm - minUMm,
                        height = maxVMm - minVMm,
                        min = new { u = minUMm, v = minVMm },
                        max = new { u = maxUMm, v = maxVMm }
                    };
                }
                else
                {
                    var typeElem = doc.GetElement(titleBlockElem.GetTypeId()) as ElementType;
                    titleBlockDto = new
                    {
                        typeName = typeElem?.Name,
                        width = (double?)null,
                        height = (double?)null,
                        min = (object)null,
                        max = (object)null
                    };
                }
            }

            // Resolve viewports
            var viewportDtos = new List<object>();
            var viewportBoxes = new List<SheetViewportBox>();
            var viewportIds = sheet.GetAllViewports().OrderBy(RevitCompat.GetId).ToArray();
            var viewportPage = ResponsePaging.Slice(viewportIds, paging.StartIndex, paging.MaxResults);
            foreach (var vpId in viewportPage.Items)
            {
                var vp = doc.GetElement(vpId) as Viewport;
                if (vp == null) continue;

                var view = doc.GetElement(vp.ViewId) as View;
                var vpIdLong = RevitCompat.GetId(vp.Id);
                var viewIdLong = RevitCompat.GetId(view?.Id);

                // GetBoxCenter returns XYZ; for sheet (paper) coordinates X=U, Y=V
                var center = vp.GetBoxCenter();
                var outline = vp.GetBoxOutline();

                double? uMm = null, vMm = null, widthMm = null, heightMm = null;
                object minDto = null, maxDto = null;
                if (center != null)
                {
                    uMm = center.X * FeetToMm;
                    vMm = center.Y * FeetToMm;
                }
                if (outline != null)
                {
                    var minUMm = outline.MinimumPoint.X * FeetToMm;
                    var minVMm = outline.MinimumPoint.Y * FeetToMm;
                    var maxUMm = outline.MaximumPoint.X * FeetToMm;
                    var maxVMm = outline.MaximumPoint.Y * FeetToMm;
                    widthMm = maxUMm - minUMm;
                    heightMm = maxVMm - minVMm;
                    minDto = new { u = minUMm, v = minVMm };
                    maxDto = new { u = maxUMm, v = maxVMm };
                    viewportBoxes.Add(new SheetViewportBox
                    {
                        VpId = vpIdLong,
                        ViewId = viewIdLong,
                        ViewName = view?.Name,
                        BoxMm = new SheetRect(minUMm, minVMm, maxUMm, maxVMm)
                    });
                }

                viewportDtos.Add(new
                {
                    vpId = vpIdLong,
                    viewId = viewIdLong,
                    viewName = view?.Name,
                    viewType = view?.ViewType.ToString(),
                    scale = view?.Scale,
                    center = (uMm.HasValue && vMm.HasValue)
                        ? (object)new { u = uMm.Value, v = vMm.Value }
                        : null,
                    width = widthMm,
                    height = heightMm,
                    min = minDto,
                    max = maxDto
                });
            }

            var diagnostics = SheetLayoutDiagnostics.Analyze(titleBlockRectMm, viewportBoxes);

            return CommandResult.Ok(new
            {
                sheetId = RevitCompat.GetId(sheet.Id),
                sheetNumber = sheet.SheetNumber,
                sheetName = sheet.Name,
                titleBlock = titleBlockDto,
                viewport_count = viewportPage.TotalCount,
                start_viewport = viewportPage.StartIndex,
                returned_viewport_count = viewportPage.ReturnedCount,
                truncated = viewportPage.Truncated,
                next_viewport = viewportPage.NextIndex,
                layout_checks_complete = viewportPage.StartIndex == 0
                    && !viewportPage.Truncated
                    && viewportBoxes.Count == viewportPage.TotalCount
                    && titleBlockRectMm.HasValue,
                box_overlaps = diagnostics.BoxOverlaps.Select(o => new
                {
                    vpIdA = o.VpIdA,
                    viewIdA = o.ViewIdA,
                    viewNameA = o.ViewNameA,
                    vpIdB = o.VpIdB,
                    viewIdB = o.ViewIdB,
                    viewNameB = o.ViewNameB,
                    overlapWidthMm = o.OverlapWidthMm,
                    overlapHeightMm = o.OverlapHeightMm
                }).ToList(),
                outside_titleblock = diagnostics.OutsideTitleBlock.Select(o => new
                {
                    vpId = o.VpId,
                    viewId = o.ViewId,
                    viewName = o.ViewName,
                    overflowLeftMm = o.OverflowLeftMm,
                    overflowRightMm = o.OverflowRightMm,
                    overflowTopMm = o.OverflowTopMm,
                    overflowBottomMm = o.OverflowBottomMm,
                    maxOverflowMm = o.MaxOverflowMm
                }).ToList(),
                viewports = viewportDtos
            });
        }
    }
}
