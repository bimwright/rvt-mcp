using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports a single view to a raster image (PNG or JPEG) using
    /// ImageExportOptions / Document.ExportImage (stable Revit 2022+).
    /// No Transaction is required for an export operation.
    /// </summary>
    public class ExportImageHandler : IRevitCommand
    {
        public string Name => "export_image";
        public string Description => "Export a view to a raster image (PNG/JPEG). Exports the active view when no view_id is given.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""output_path""],
  ""properties"":{
    ""output_path"":{""type"":""string"",""description"":""Absolute file path including extension (.png or .jpg).""},
    ""view_id"":{""type"":""integer"",""description"":""View ElementId. If omitted, the active view.""},
    ""pixel_size"":{""type"":""integer"",""default"":2048,""description"":""Pixel size of the longer image dimension.""},
    ""image_format"":{""type"":""string"",""enum"":[""png"",""jpeg""],""default"":""png"",""description"":""Output raster format.""}
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
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var outputPath = request.Value<string>("output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
                return CommandResult.Fail("output_path is required.");

            if (!Path.IsPathRooted(outputPath))
                return CommandResult.Fail("output_path must be an absolute rooted path: " + outputPath);

            string parentDir;
            try
            {
                parentDir = Path.GetDirectoryName(outputPath);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("output_path is not a valid path: " + ex.Message);
            }

            if (string.IsNullOrEmpty(parentDir))
                return CommandResult.Fail("output_path must include a parent directory: " + outputPath);

            if (!Directory.Exists(parentDir))
                return CommandResult.Fail("output_path parent directory does not exist: " + parentDir);

            // Pixel size of the longer image dimension.
            var pixelSize = request.Value<int?>("pixel_size") ?? 2048;
            if (pixelSize <= 0)
                return CommandResult.Fail("pixel_size must be a positive integer.");

            // Image format: png (default) or jpeg.
            var imageFormat = (request.Value<string>("image_format") ?? "png").Trim().ToLowerInvariant();
            ImageFileType fileType;
            if (imageFormat == "png")
                fileType = ImageFileType.PNG;
            else if (imageFormat == "jpeg" || imageFormat == "jpg")
                fileType = ImageFileType.JPEGLossless;
            else
                return CommandResult.Fail("image_format must be 'png' or 'jpeg'. Got: " + imageFormat);

            // Resolve the view; fall back to the active view when no view_id given.
            View view;
            var viewIdToken = request["view_id"];
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                long rawId;
                try
                {
                    rawId = viewIdToken.Value<long>();
                }
                catch (Exception)
                {
                    return CommandResult.Fail("view_id must be an integer. Invalid value: " + viewIdToken);
                }

                if (!RevitCompat.CanRepresentElementId(rawId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(rawId));

                view = doc.GetElement(RevitCompat.ToElementId(rawId)) as View;
                if (view == null)
                    return CommandResult.Fail("View with ID " + rawId + " not found.");
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return CommandResult.Fail("No active view to export and no view_id was provided.");
            }

            if (view.IsTemplate)
                return CommandResult.Fail("View '" + view.Name + "' is a view template and cannot be exported.");

            var viewId = RevitCompat.GetId(view.Id);
            var viewName = view.Name;

            // Revit appends a suffix to the supplied path; export without the
            // extension and diff the parent directory to find the produced file.
            var pathNoExt = Path.Combine(parentDir, Path.GetFileNameWithoutExtension(outputPath));

            HashSet<string> beforeFiles;
            try
            {
                beforeFiles = new HashSet<string>(
                    Directory.GetFiles(parentDir, "*", SearchOption.TopDirectoryOnly),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Unable to read output directory: " + ex.Message);
            }

            var exportStartUtc = DateTime.UtcNow;

            try
            {
                var opts = new ImageExportOptions();
                opts.ExportRange = ExportRange.SetOfViews;
                opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                opts.FilePath = pathNoExt;
                opts.HLRandWFViewsFileType = fileType;
                opts.ShadowViewsFileType = fileType;
                opts.ImageResolution = ImageResolution.DPI_300;
                opts.PixelSize = pixelSize;
                opts.FitDirection = FitDirectionType.Horizontal;
                opts.ZoomType = ZoomFitType.FitToPage;

                doc.ExportImage(opts);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Image export failed: " + ex.Message);
            }

            // Identify the produced image by comparing the directory before/after.
            // Revit appends a view-name suffix to the base file name.
            string producedPath = null;
            try
            {
                var newFiles = Directory.GetFiles(parentDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => !beforeFiles.Contains(f))
                    .ToArray();

                var baseName = Path.GetFileNameWithoutExtension(outputPath);
                var match = newFiles
                    .Where(f => Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => Path.GetFileName(f).Length)
                    .FirstOrDefault();

                if (string.IsNullOrEmpty(match))
                    match = FindExistingImageCandidate(outputPath, parentDir, baseName, exportStartUtc);

                if (!string.IsNullOrEmpty(match) && File.Exists(match))
                    producedPath = match;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Export completed but reading output directory failed: " + ex.Message);
            }

            if (string.IsNullOrEmpty(producedPath))
            {
                return CommandResult.Ok(new
                {
                    exported = false,
                    output_path = outputPath,
                    view_id = viewId,
                    view_name = viewName,
                    pixel_size = pixelSize,
                    error = "Image export produced no file on disk."
                });
            }

            return CommandResult.Ok(new
            {
                exported = true,
                output_path = producedPath,
                view_id = viewId,
                view_name = viewName,
                pixel_size = pixelSize,
                error = (string)null
            });
        }

        private static string FindExistingImageCandidate(string outputPath, string parentDir, string baseName, DateTime exportStartUtc)
        {
            var thresholdUtc = exportStartUtc.AddSeconds(-1);
            try
            {
                if (File.Exists(outputPath) && File.GetLastWriteTimeUtc(outputPath) >= thresholdUtc)
                    return outputPath;
            }
            catch { }

            try
            {
                return Directory.GetFiles(parentDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => Path.GetFileName(f).StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    .Where(f =>
                    {
                        try { return File.GetLastWriteTimeUtc(f) >= thresholdUtc; }
                        catch { return false; }
                    })
                    .OrderByDescending(f => Path.GetFileName(f).Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
