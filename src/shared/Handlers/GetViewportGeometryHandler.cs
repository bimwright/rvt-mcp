using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetViewportGeometryHandler : IRevitCommand
    {
        public string Name => "get_viewport_geometry";
        public string Description => "Read viewport positions on a sheet in sheet-space millimetres: visible crop rectangle, view outline, scale, rotation, pinned and crop flags.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""sheet_id"": { ""type"": ""integer"" },
    ""sheet_number"": { ""type"": ""string"" },
    ""viewport_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""start_viewport"": { ""type"": ""integer"", ""default"": 0, ""minimum"": 0 },
    ""max_viewports"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 1, ""maximum"": 500 }
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
                return CommandResult.Fail($"Parameters must be a JSON object: {ex.Message}");
            }

            if (!ResponsePaging.TryParse(request, "start_viewport", "max_viewports", 100, 500, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);

            if (!ViewportSheetSupport.TryResolveSheet(doc, request, out var sheet, out var sheetError))
                return CommandResult.Fail(sheetError);

            var viewports = ViewportSheetSupport.GetViewports(doc, sheet);

            if (request["viewport_ids"] != null && request["viewport_ids"].Type != JTokenType.Null)
            {
                if (!(request["viewport_ids"] is JArray idArray) || idArray.Count == 0)
                    return CommandResult.Fail("viewport_ids must be a non-empty array of viewport ids when supplied.");

                var wanted = new HashSet<long>();
                foreach (var token in idArray)
                {
                    if (token.Type != JTokenType.Integer)
                        return CommandResult.Fail("viewport_ids must contain integers.");
                    wanted.Add(token.Value<long>());
                }

                var onSheet = new HashSet<long>(viewports.Select(vp => RevitCompat.GetId(vp.Id)));
                var unknown = wanted.Where(id => !onSheet.Contains(id)).OrderBy(id => id).ToArray();
                if (unknown.Length > 0)
                    return CommandResult.Fail("Viewport id(s) not on sheet " + sheet.SheetNumber + ": " + string.Join(", ", unknown));

                viewports = viewports.Where(vp => wanted.Contains(RevitCompat.GetId(vp.Id))).ToList();
            }

            var page = ResponsePaging.Slice(viewports, paging.StartIndex, paging.MaxResults);
            var items = page.Items.Select(vp => ViewportSheetSupport.SnapshotDto(ViewportSheetSupport.Read(doc, vp))).ToList();

            return CommandResult.Ok(new
            {
                sheet_id = RevitCompat.GetId(sheet.Id),
                sheet_number = sheet.SheetNumber,
                sheet_name = sheet.Name,
                units = "mm",
                coordinate_frame = ViewportSheetSupport.CoordinateFrame,
                alignment_rect_note = ViewportSheetSupport.AlignmentRectNote,
                sheet_frame_sheet_mm = ViewportSheetSupport.RectDto(ViewportSheetSupport.GetSheetFrame(doc, sheet)),
                viewport_count = page.TotalCount,
                start_viewport = page.StartIndex,
                returned_viewport_count = page.ReturnedCount,
                truncated = page.Truncated,
                next_viewport = page.NextIndex,
                viewports = items
            });
        }
    }
}
