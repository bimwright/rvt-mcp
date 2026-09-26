using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class OpenModelHandler : IRevitCommand
    {
        public string Name => "open_model";

        public string Description => "Open a Revit model, template or family from disk and make it the active document";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""Absolute path to a .rvt/.rte/.rfa on the machine Revit runs on""},""activate"":{""type"":""boolean"",""description"":""Open in the UI and make it active (default true). False opens it in the background with no view.""},""detach"":{""type"":""boolean"",""description"":""Detach a workshared model from central, preserving worksets (default false)""},""audit"":{""type"":""boolean"",""description"":""Audit the file while opening - slow, use on a suspect model (default false)""},""worksets"":{""type"":""string"",""description"":""all|none|lastViewed - which worksets to open. Workshared models only.""}},""required"":[""path""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = JObject.Parse(paramsJson);
            var path = request.Value<string>("path");

            if (string.IsNullOrWhiteSpace(path))
                return CommandResult.Fail("path parameter is required: an absolute path to a .rvt, .rte or .rfa on the machine Revit runs on.");

            path = path.Trim().Trim('"');

            if (!File.Exists(path))
                return CommandResult.Fail("No file at '" + path + "'. The path must be absolute and reachable from the Revit process, not from the MCP client.");

            var extension = Path.GetExtension(path);
            if (!IsRevitFile(extension))
                return CommandResult.Fail("'" + extension + "' is not a Revit file. Expected .rvt (project), .rte (template) or .rfa (family).");

            var alreadyOpen = app.Application.Documents
                .Cast<Document>()
                .FirstOrDefault(d => string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));

            if (alreadyOpen != null)
            {
                return CommandResult.Ok(new
                {
                    opened = false,
                    was_already_open = true,
                    title = alreadyOpen.Title,
                    path = alreadyOpen.PathName,
                    note = "Revit already has this model open; it was left as it is. Close it first if you need a fresh open."
                });
            }

            var activate = request.Value<bool?>("activate") ?? true;
            var detach = request.Value<bool?>("detach") ?? false;
            var audit = request.Value<bool?>("audit") ?? false;
            var worksets = request.Value<string>("worksets");

            string savedInVersion = null;
            var isWorkshared = false;
            try
            {
                var info = BasicFileInfo.Extract(path);
                savedInVersion = info.Format;
                isWorkshared = info.IsWorkshared;
            }
            catch
            {
                // Let the open below produce the real diagnosis.
            }

            if (detach && !isWorkshared)
                return CommandResult.Fail("'" + Path.GetFileName(path) + "' is not workshared, so it cannot be detached from central. Call again without detach.");

            if (!string.IsNullOrWhiteSpace(worksets) && !isWorkshared)
                return CommandResult.Fail("'" + Path.GetFileName(path) + "' is not workshared, so it has no worksets to configure. Call again without worksets.");

            var options = new OpenOptions
            {
                Audit = audit,
                DetachFromCentralOption = detach
                    ? DetachFromCentralOption.DetachAndPreserveWorksets
                    : DetachFromCentralOption.DoNotDetach
            };

            if (!string.IsNullOrWhiteSpace(worksets))
            {
                WorksetConfigurationOption worksetOption;
                switch (worksets.Trim().ToLowerInvariant())
                {
                    case "all": worksetOption = WorksetConfigurationOption.OpenAllWorksets; break;
                    case "none": worksetOption = WorksetConfigurationOption.CloseAllWorksets; break;
                    case "lastviewed": worksetOption = WorksetConfigurationOption.OpenLastViewed; break;
                    default:
                        return CommandResult.Fail("worksets was '" + worksets + "'. Expected all, none or lastViewed.");
                }
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(worksetOption));
            }

            try
            {
                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);

                Document document;
                string activeViewName = null;

                if (activate)
                {
                    // detachAndPrompt: true shows a modal dialog nobody is there to answer.
                    var uidoc = app.OpenAndActivateDocument(modelPath, options, false);
                    document = uidoc.Document;
                    activeViewName = uidoc.ActiveView != null ? uidoc.ActiveView.Name : null;
                }
                else
                {
                    document = app.Application.OpenDocumentFile(modelPath, options);
                }

                return CommandResult.Ok(new
                {
                    opened = true,
                    was_already_open = false,
                    title = document.Title,
                    path = document.PathName,
                    saved_in_version = savedInVersion,
                    is_workshared = document.IsWorkshared,
                    is_family = document.IsFamilyDocument,
                    detached = detach,
                    activated = activate,
                    active_view = activeViewName,
                    note = activate
                        ? "This is now the active document; every other tool reads it."
                        : "Opened in the background with no view. It is NOT the active document, so tools reading the active document will not see it."
                });
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                return CommandResult.Fail("Revit refused to open '" + Path.GetFileName(path) + "': " + ex.Message);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Could not open '" + Path.GetFileName(path) + "': " + ex.Message);
            }
        }

        private static bool IsRevitFile(string extension)
        {
            return string.Equals(extension, ".rvt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".rte", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".rfa", StringComparison.OrdinalIgnoreCase);
        }
    }
}
