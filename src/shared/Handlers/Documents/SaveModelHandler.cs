using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// A document opened from a .rte template has no project file yet, so it needs save_as_path; that is
    /// how open_model + save_model makes a new project. Workshared models are refused because
    /// synchronizing with central stays a human decision.
    /// </summary>
    public class SaveModelHandler : IRevitCommand
    {
        public string Name => "save_model";

        public string Description => "Save the active document, or save it as a new .rvt (required for a document opened from a .rte template)";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""save_as_path"":{""type"":""string"",""description"":""Absolute .rvt path to save to. Omit to save in place.""},""overwrite"":{""type"":""boolean"",""description"":""Allow replacing an existing file at save_as_path (default false)""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No active document. Open a model or template with revit_open_model first.");
            if (doc.IsFamilyDocument)
                return CommandResult.Fail("The active document is a family; save_model only saves projects.");
            if (doc.IsWorkshared)
                return CommandResult.Fail("The active model is workshared. Saving and synchronizing with central are left to the user.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var target = request.Value<string>("save_as_path")?.Trim().Trim('"');
            var overwrite = request.Value<bool?>("overwrite") ?? false;
            var currentPath = doc.PathName ?? "";
            var fromTemplate = currentPath.EndsWith(".rte", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(target))
            {
                if (SaveModelPolicy.NeedsSaveAsPath(currentPath))
                    return CommandResult.Fail("This document has no project file yet (new or opened from a template). Pass save_as_path: the full local path of the project file to create.");
                try
                {
                    doc.Save();
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Revit refused to save: " + ex.Message);
                }
                return CommandResult.Ok(new { saved = true, path = currentPath, mode = "save" });
            }

            var pathError = SaveModelPolicy.CheckSaveAsPath(target);
            if (pathError != null) return CommandResult.Fail(pathError);
            target = Path.GetFullPath(target);
            foreach (Document open in app.Application.Documents)
            {
                if (!open.Equals(doc) && OpenModelLocalPolicy.PathsEqual(open.PathName, target))
                    return CommandResult.Fail("'" + target + "' is the file of another open document ('" + open.Title + "'). Pick another path.");
            }
            var folder = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return CommandResult.Fail("The folder '" + folder + "' does not exist on the Revit machine.");
            if (File.Exists(target) && !overwrite)
                return CommandResult.Fail("A file already exists at '" + target + "'. Pass overwrite=true to replace it.");

            try
            {
                doc.SaveAs(target, new SaveAsOptions { OverwriteExistingFile = overwrite });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Revit refused to save: " + ex.Message);
            }
            return CommandResult.Ok(new { saved = true, path = doc.PathName, mode = "save_as", from_template = fromTemplate });
        }
    }
}
