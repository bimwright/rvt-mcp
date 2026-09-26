using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// open_model: open a .rvt/.rte/.rfa from disk. A workshared .rvt is never
    /// opened directly — a new local copy is always created from its central
    /// model (same as Revit's Open → "Create New Local") so an agent call can
    /// never write into a central file. Detaching or opening a central directly
    /// is a send_code job by design (docs/design rule §2.4).
    /// Opening never saves the model.
    /// </summary>
    public class OpenModelHandler : IRevitCommand
    {
        public string Name => "open_model";

        public string Description => "Open a Revit model, template or family from disk; workshared models always open via Create New Local from their central model";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""Absolute path to a .rvt/.rte/.rfa on the machine Revit runs on""},""activate"":{""type"":""boolean"",""description"":""Open in the UI and make it active (default true). False opens it in the background with no view.""},""audit"":{""type"":""boolean"",""description"":""Audit the file while opening - slow, use on a suspect model (default false)""},""worksets"":{""type"":""string"",""description"":""all|none|lastViewed - which worksets to open. Workshared models only; applies to the new local copy.""}},""required"":[""path""]}";

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

            var activate = request.Value<bool?>("activate") ?? true;
            var audit = request.Value<bool?>("audit") ?? false;
            var worksets = request.Value<string>("worksets");

            // Classify the file from its header before opening anything.
            string savedInVersion = null;
            string headerWorksharing = null;
            string centralPath = null;
            var isWorkshared = false;
            var headerRead = false;
            string headerError = null;
            try
            {
                var info = BasicFileInfo.Extract(path);
                savedInVersion = info.Format;
                isWorkshared = info.IsWorkshared;
                headerWorksharing = !info.IsWorkshared ? "not_enabled"
                    : info.IsCentral ? "central"
                    : info.IsLocal ? "local"
                    : "workshared";
                if (info.IsWorkshared)
                    centralPath = info.CentralPath;
                headerRead = true;
            }
            catch (Exception ex)
            {
                headerError = ex.Message;
            }

            // Rule §2.1: an unreadable header means worksharing is UNKNOWN, and
            // unknown is not "not workshared" - a possibly-workshared file is
            // never opened directly. The worksets checks ride the same tested
            // decision so the refusal ordering cannot silently regress.
            switch (OpenModelLocalPolicy.DecideInput(headerRead, isWorkshared, worksets))
            {
                case OpenModelLocalPolicy.InputDecision.RefuseUnclassified:
                    return CommandResult.Fail("Could not read the header of '" + Path.GetFileName(path) + "' (" + headerError + "), so its worksharing state is unknown. The file is left closed and untouched - a possibly-workshared file is never opened directly. If the file is healthy, open it once in Revit; otherwise handle it via revit_send_code_to_revit.");
                case OpenModelLocalPolicy.InputDecision.RefuseBadWorksets:
                    return CommandResult.Fail("worksets was '" + worksets + "'. Expected all, none or lastViewed.");
                case OpenModelLocalPolicy.InputDecision.RefuseWorksetsOnPlainFile:
                    return CommandResult.Fail("'" + Path.GetFileName(path) + "' is not workshared, so it has no worksets to configure. Call again without worksets.");
            }

            // A model loaded only as a link (IsLinked) must not count as open.
            // For workshared input, match any open doc on the same central —
            // an open local copy's PathName is the local file, not the path given.
            Document alreadyOpen = null;
            Document linkedMatch = null;
            foreach (Document d in app.Application.Documents)
            {
                var samePath = PathsEqual(d.PathName, path);
                if (d.IsLinked)
                {
                    // Loaded as a link: not "open", but Revit cannot open the same
                    // file standalone either — OpenDocumentFile returns the link
                    // Document and OpenAndActivateDocument throws. Report it.
                    if (samePath) linkedMatch = d;
                    continue;
                }
                if (samePath) { alreadyOpen = d; break; }
                if (isWorkshared && !string.IsNullOrWhiteSpace(centralPath)
                    && CentralPathEquals(d, centralPath)) { alreadyOpen = d; break; }
            }

            if (alreadyOpen != null)
                return FinishAlreadyOpen(app, alreadyOpen, activate, savedInVersion);

            if (linkedMatch != null)
                return CommandResult.Fail("'" + Path.GetFileName(path) + "' is currently loaded as a link inside an open document, and Revit cannot open it as a standalone document while it is linked. Close the host document or unload the link first, then retry.");

            var options = new OpenOptions { Audit = audit };
            if (!string.IsNullOrWhiteSpace(worksets))
            {
                WorksetConfigurationOption worksetOption;
                switch (worksets.Trim().ToLowerInvariant())
                {
                    case "all": worksetOption = WorksetConfigurationOption.OpenAllWorksets; break;
                    case "none": worksetOption = WorksetConfigurationOption.CloseAllWorksets; break;
                    default: worksetOption = WorksetConfigurationOption.OpenLastViewed; break;
                }
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(worksetOption));
            }

            string localPath = null;
            string renamedTo = null;
            if (isWorkshared)
            {
                if (string.IsNullOrWhiteSpace(centralPath))
                    return CommandResult.Fail("'" + Path.GetFileName(path) + "' is workshared but its header has no Central Model Path. Ask the user to open it once in Revit, or handle it via revit_send_code_to_revit.");

                if (!File.Exists(centralPath))
                    return CommandResult.Fail("This file is workshared and its central model is not reachable at '" + centralPath + "' (offline share, missing drive map or VPN down). Ask the user to check connectivity; do not retry until the path resolves.");

                var configuredDir = GetLocalProjectsDir(app, out var revitIniPath);
                if (OpenModelLocalPolicy.DecideLocalDir(configuredDir) == OpenModelLocalPolicy.LocalDirDecision.StopMissing)
                    return CommandResult.Fail("No ProjectPath entry in this Revit's Revit.ini ('" + revitIniPath + "'), so there is no configured local-projects folder. The user can set one under File > Options > File Locations, or manage local copies by hand via revit_send_code_to_revit.");
                var localDir = configuredDir;
                try { Directory.CreateDirectory(localDir); }
                catch (Exception ex) { return CommandResult.Fail("Could not create the local-projects folder '" + localDir + "': " + ex.Message); }

                var target = Path.Combine(localDir,
                    Path.GetFileNameWithoutExtension(centralPath) + "_" + app.Application.Username + ".rvt");

                if (File.Exists(target))
                {
                    // The name only encodes the central's file name, so a local
                    // for a DIFFERENT central can sit (or be open) at this path.
                    // Rule §2.5: a file open in this session - as a document or
                    // only as a loaded link - is never touched.
                    var targetOpenInSession = false;
                    foreach (Document d in app.Application.Documents)
                    {
                        if (OpenModelLocalPolicy.PathsEqual(d.PathName, target)) { targetOpenInSession = true; break; }
                    }

                    bool oldHeaderRead = false, oldWorkshared = false, oldSynced = true;
                    try
                    {
                        var oldInfo = BasicFileInfo.Extract(target);
                        oldHeaderRead = true;
                        oldWorkshared = oldInfo.IsWorkshared;
                        oldSynced = oldInfo.AllLocalChangesSavedToCentral;
                    }
                    catch { /* unreadable - Decide below treats it as unverifiable */ }

                    switch (OpenModelLocalPolicy.Decide(true, targetOpenInSession, oldHeaderRead, oldWorkshared, oldSynced))
                    {
                        case OpenModelLocalPolicy.CollisionDecision.StopOpenInSession:
                            return CommandResult.Fail("'" + target + "' is open in this Revit session (as a document or a loaded link) and is left untouched. Close it first - note it may belong to a different central model with the same file name.");
                        case OpenModelLocalPolicy.CollisionDecision.StopUnsynced:
                            return CommandResult.Fail("'" + target + "' already exists and has local changes not yet saved to central. It is left untouched - ask the user whether to sync or discard it before retrying.");
                        case OpenModelLocalPolicy.CollisionDecision.StopUnverifiable:
                            return CommandResult.Fail("Could not read the header of the existing local '" + target + "', so whether it still holds unsynced work is unknown. It is left untouched - ask the user to inspect or move it manually before retrying.");
                    }

                    renamedTo = Path.Combine(localDir,
                        Path.GetFileNameWithoutExtension(target) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".rvt");
                    try { File.Move(target, renamedTo); }
                    catch (Exception ex)
                    {
                        return CommandResult.Fail("Could not rename the existing local '" + target + "' (is it open in another Revit or locked?): " + ex.Message + ". Nothing was changed.");
                    }
                }

                var renameNote = renamedTo != null
                    ? " The existing local was moved aside to '" + renamedTo + "' and is untouched."
                    : "";
                try
                {
                    var centralModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(centralPath);
                    var targetModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(target);
                    WorksharingUtils.CreateNewLocal(centralModelPath, targetModelPath);
                    localPath = target;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    return CommandResult.Fail("Revit refused to create a local copy of '" + Path.GetFileName(centralPath) + "': " + ex.Message + renameNote);
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Could not create a local copy of '" + Path.GetFileName(centralPath) + "': " + ex.Message + renameNote);
                }
            }

            try
            {
                var openPath = isWorkshared ? localPath : path;
                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(openPath);

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
                    header_worksharing = headerWorksharing,
                    central_path = centralPath,
                    local_path = localPath,
                    local_file_created = localPath != null,
                    renamed_existing_local = renamedTo,
                    audit,
                    worksets,
                    activated = activate,
                    active_view = activeViewName,
                    note = activate
                        ? "This is now the active document; every other tool reads it."
                        : "Opened in the background with no view. It is NOT the active document, so tools reading the active document will not see it."
                });
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                return CommandResult.Fail("Revit refused to open '" + Path.GetFileName(path) + "'" +
                    (localPath != null ? " (the new local copy '" + localPath + "' was created but could not be opened)" : "") +
                    ": " + ex.Message);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Could not open '" + Path.GetFileName(path) + "'" +
                    (localPath != null ? " (the new local copy '" + localPath + "' was created but could not be opened)" : "") +
                    ": " + ex.Message);
            }
        }

        private CommandResult FinishAlreadyOpen(UIApplication app, Document alreadyOpen, bool activate, string savedInVersion)
        {
            var activated = false;
            if (activate)
            {
                var activeDoc = app.ActiveUIDocument?.Document;
                activated = activeDoc != null && PathsEqual(activeDoc.PathName, alreadyOpen.PathName);
                if (!activated)
                {
                    // ActiveUIDocument is read-only for external commands; reopening an
                    // already-open document just activates it.
                    try
                    {
                        app.OpenAndActivateDocument(
                            ModelPathUtils.ConvertUserVisiblePathToModelPath(alreadyOpen.PathName),
                            new OpenOptions(), false);
                        activated = true;
                    }
                    catch (Exception ex)
                    {
                        return CommandResult.Fail("'" + Path.GetFileName(alreadyOpen.PathName) + "' is already open but could not be activated: " + ex.Message);
                    }
                }
            }

            return CommandResult.Ok(new
            {
                opened = false,
                was_already_open = true,
                activated,
                title = alreadyOpen.Title,
                path = alreadyOpen.PathName,
                saved_in_version = savedInVersion,
                is_workshared = alreadyOpen.IsWorkshared,
                is_family = alreadyOpen.IsFamilyDocument,
                note = activate
                    ? "This model was already open and is now the active document."
                    : "Revit already has this model open; it was left as it is. Call again with activate=true to make it the active document."
            });
        }

        private static bool CentralPathEquals(Document doc, string centralPath)
        {
            try
            {
                if (PathsEqual(doc.PathName, centralPath)) return true;
                if (!doc.IsWorkshared) return false;
                var cp = doc.GetWorksharingCentralModelPath();
                return cp != null && !cp.Empty
                    && PathsEqual(ModelPathUtils.ConvertModelPathToUserVisiblePath(cp), centralPath);
            }
            catch { return false; }
        }

        /// <summary>
        /// The local-projects folder is the ProjectPath key in this Revit
        /// version's own Revit.ini (design doc §4.2). Parsing lives in
        /// OpenModelLocalPolicy; missing or unreadable resolves to null, which
        /// the caller turns into a reported stop - never a fallback folder.
        /// </summary>
        private static string GetLocalProjectsDir(UIApplication app, out string revitIniPath)
        {
            revitIniPath = Path.Combine(app.Application.CurrentUsersDataFolderPath, "Revit.ini");
            try
            {
                if (File.Exists(revitIniPath))
                    return OpenModelLocalPolicy.ParseProjectPath(File.ReadAllLines(revitIniPath));
            }
            catch { }

            return null;
        }

        private static bool PathsEqual(string a, string b)
        {
            return OpenModelLocalPolicy.PathsEqual(a, b);
        }

        private static bool IsRevitFile(string extension)
        {
            return string.Equals(extension, ".rvt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".rte", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".rfa", StringComparison.OrdinalIgnoreCase);
        }
    }
}
