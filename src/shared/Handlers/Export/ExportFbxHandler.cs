using System;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Exports a single 3D view from the active document to an Autodesk FBX file
    /// (consumed by external rendering / animation tools). No transaction is used —
    /// export is a read-only document operation. FBX export requires a 3D view:
    /// the view is resolved by view_id or, when omitted, from the active view, and
    /// must be a View3D. The export uses a ViewSet containing exactly that one view.
    /// </summary>
    public class ExportFbxHandler : IRevitCommand
    {
        public string Name => "export_fbx";

        public string Description =>
            "Export a 3D view to an Autodesk FBX file (for rendering / animation tools). " +
            "Provide an absolute output_folder that already exists and a file_name without extension. " +
            "If view_id is omitted, the active view must itself be a 3D view. FBX export requires a 3D view.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""output_folder"", ""file_name""],
  ""properties"": {
    ""output_folder"": {""type"": ""string"", ""description"": ""Absolute folder path. Must exist.""},
    ""file_name"": {""type"": ""string"", ""description"": ""Output .fbx file name (without extension).""},
    ""view_id"": {""type"": ""integer"", ""description"": ""3D view ElementId. If omitted, the active view must be a 3D view.""}
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
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var outputFolder = request.Value<string>("output_folder");
            var fileName = request.Value<string>("file_name");

            // Validate output folder.
            if (string.IsNullOrWhiteSpace(outputFolder))
                return BuildErrorDto(null, null, "Parameter 'output_folder' is required.");

            if (!Path.IsPathRooted(outputFolder))
                return BuildErrorDto(null, null,
                    "output_folder must be an absolute path (e.g. C:\\... or D:\\...). Relative paths are rejected.");

            if (!Directory.Exists(outputFolder))
                return BuildErrorDto(null, null, "output_folder does not exist: " + outputFolder);

            // Validate file name.
            if (string.IsNullOrWhiteSpace(fileName))
                return BuildErrorDto(null, null, "Parameter 'file_name' is required.");

            // Strip a trailing .fbx extension if the caller supplied one anyway.
            var baseName = fileName.Trim();
            if (baseName.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                baseName = baseName.Substring(0, baseName.Length - 4);

            if (string.IsNullOrWhiteSpace(baseName))
                return BuildErrorDto(null, null, "Parameter 'file_name' must contain a usable file name.");

            if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return BuildErrorDto(null, null,
                    "file_name contains characters that are invalid for a file name.");

            // Resolve the 3D view (by view_id or active view).
            View3D view3d;
            var viewIdToken = request["view_id"];
            if (viewIdToken != null && viewIdToken.Type != JTokenType.Null)
            {
                long rawId;
                if (viewIdToken.Type == JTokenType.Integer)
                {
                    rawId = viewIdToken.Value<long>();
                }
                else
                {
                    var asStr = viewIdToken.Value<string>();
                    if (!long.TryParse((asStr ?? string.Empty).Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out rawId))
                        return BuildErrorDto(null, null,
                            "view_id must be a numeric element id (got '" + (asStr ?? "<null>") + "').");
                }

                if (!RevitCompat.CanRepresentElementId(rawId))
                    return BuildErrorDto(null, null, RevitCompat.ElementIdRangeError(rawId));

                var elId = RevitCompat.ToElementId(rawId);
                var element = doc.GetElement(elId);
                if (element == null)
                    return BuildErrorDto(null, null,
                        "No element found with id " + rawId.ToString(CultureInfo.InvariantCulture) + ".");

                view3d = element as View3D;
                if (view3d == null)
                    return BuildErrorDto(null, null,
                        "FBX export requires a 3D view. Element id "
                        + rawId.ToString(CultureInfo.InvariantCulture) + " is not a 3D view.");
            }
            else
            {
                var activeView = doc.ActiveView;
                if (activeView == null)
                    return BuildErrorDto(null, null,
                        "No view_id supplied and there is no active view to export.");

                view3d = activeView as View3D;
                if (view3d == null)
                    return BuildErrorDto(null, null,
                        "FBX export requires a 3D view. The active view is not a 3D view; "
                        + "supply view_id of a 3D view or activate one.");
            }

            if (view3d.IsTemplate)
                return BuildErrorDto(null, null,
                    "The resolved 3D view is a view template and cannot be exported.");

            var viewId = RevitCompat.GetId(view3d.Id);
            var viewName = SafeName(view3d);

            // Build a ViewSet containing exactly the one 3D view.
            var views = new ViewSet();
            views.Insert(view3d);

            var outputPath = Path.Combine(outputFolder, baseName + ".fbx");

            // Export. Overload: Export(string folder, string name, ViewSet views, FBXExportOptions options).
            try
            {
                var opts = new FBXExportOptions();
                doc.Export(outputFolder, baseName, views, opts);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException revitEx)
            {
                return BuildErrorDto(viewId, viewName, "FBX export failed: " + revitEx.Message);
            }
            catch (Exception ex)
            {
                return BuildErrorDto(viewId, viewName, "FBX export failed: " + ex.Message);
            }

            // Verify the .fbx file exists after export.
            if (!File.Exists(outputPath))
                return BuildErrorDto(viewId, viewName,
                    "FBX export reported success but the expected file was not found: " + outputPath);

            return CommandResult.Ok(new
            {
                exported = true,
                output_path = outputPath,
                view_id = viewId,
                view_name = viewName,
                error = (string)null
            });
        }

        private static CommandResult BuildErrorDto(long? viewId, string viewName, string error)
        {
            return CommandResult.Ok(new
            {
                exported = false,
                output_path = (string)null,
                view_id = viewId,
                view_name = viewName,
                error
            });
        }

        private static string SafeName(Element element)
        {
            if (element == null) return null;
            try { return element.Name; }
            catch { return null; }
        }
    }
}
