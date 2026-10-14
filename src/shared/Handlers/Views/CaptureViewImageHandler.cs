using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CaptureViewImageHandler : IRevitCommand
    {
        public string Name => "capture_view_image";
        public string Description => "Export a view to a raster image (png/jpeg). output_path is optional; if provided it must be absolute and inside %TEMP% or %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\captures\\. Returns saved path + pixel size.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""output_path"":{""type"":""string"",""description"":""Optional absolute path inside %TEMP% or %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\captures\\. Defaults to a generated name under captures.""},""pixel_size"":{""type"":""integer"",""default"":1600},""image_format"":{""type"":""string"",""enum"":[""png"",""jpeg""],""default"":""png""}},""required"":[]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var rawOutputPath = req.Value<string>("output_path");
            var pixelSize = req.Value<int?>("pixel_size") ?? 1600;
            var imageFormat = (req.Value<string>("image_format") ?? "png").ToLowerInvariant();

            if (imageFormat != "png" && imageFormat != "jpeg")
                return CommandResult.Fail("image_format must be 'png' or 'jpeg'.");

            var outputPath = CaptureOutputPath.NormalizeOrDefault(rawOutputPath, imageFormat);
            var pathError = CaptureOutputPath.Validate(outputPath);
            if (pathError != null) return CommandResult.Fail(pathError);

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");
            if (view.IsTemplate) return CommandResult.Fail("Cannot export a view template.");

            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory)) return CommandResult.Fail("output_path must include a directory.");
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var options = new ImageExportOptions
            {
                FilePath = outputPath,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = pixelSize,
                FitDirection = FitDirectionType.Horizontal,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = imageFormat == "jpeg" ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                ShadowViewsFileType = imageFormat == "jpeg" ? ImageFileType.JPEGLossless : ImageFileType.PNG,
                ExportRange = ExportRange.SetOfViews
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });

            string exportError = null;
            try
            {
                doc.ExportImage(options);
            }
            catch (Exception ex)
            {
                exportError = ex.Message;
            }

            var actualPath = CaptureOutputResolver.FindActualOutput(outputPath, imageFormat);
            if (actualPath == null)
            {
                if (!string.IsNullOrEmpty(exportError))
                    return CommandResult.Fail($"ExportImage failed: {exportError}");
                return CommandResult.Fail("ExportImage completed but output file was not found.");
            }

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id),
                saved_path = actualPath,
                pixel_size = pixelSize,
                image_format = imageFormat
            });
        }
    }
}
