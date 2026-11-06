using System;

namespace RvtMcp.Plugin.Documents
{
    public sealed class DocumentPolicy
    {
        public string Mode { get; private set; }
        public string[] Rules { get; private set; }
        public string[] Allowed { get; private set; }

        public static DocumentPolicy ForMode(string mode)
        {
            switch (mode)
            {
                case "local_file":
                case "family":
                    return new DocumentPolicy
                    {
                        Mode = mode,
                        Rules = new[]
                        {
                            "Save overwrites this file; Revit retains the previous version as a backup. The first save through this tool ensures at least 20 backups and preserves a known higher limit.",
                            "Create a checkpoint only before risky work, with user approval, or at handover.",
                            "Do not prune or restore without user approval.",
                            "Report unsaved changes when work ends."
                        },
                        Allowed = new[] { "revit_get_document_state", "revit_save_document", "revit_save_checkpoint", "revit_manage_checkpoints" }
                    };
                case "workshared_local":
                    return new DocumentPolicy
                    {
                        Mode = mode,
                        Rules = new[] { "Save affects only the local file; changes are not synced to central.", "Sync and checkpoints are not supported.", "Tell the user about unsynced changes to central." },
                        Allowed = new[] { "revit_get_document_state", "revit_save_document" }
                    };
                case "workshared_central":
                    return new DocumentPolicy
                    {
                        Mode = mode,
                        Rules = new[] { "This is the central file; do not save it directly.", "Ask the user to reopen it as a local file with revit_open_model." },
                        Allowed = new[] { "revit_get_document_state" }
                    };
                case "cloud":
                    return new DocumentPolicy
                    {
                        Mode = mode,
                        Rules = new[] { "Cloud models are not supported.", "Ask the user to save in Revit." },
                        Allowed = new[] { "revit_get_document_state" }
                    };
                case "unsaved":
                    return new DocumentPolicy
                    {
                        Mode = mode,
                        Rules = new[]
                        {
                            "This document has not been saved.",
                            "Save it with revit_save_document(save_as_path) only to a new local .rvt path the user gave; never choose the path yourself. Otherwise ask the user to use Save As in Revit."
                        },
                        Allowed = new[] { "revit_get_document_state", "revit_save_document" }
                    };
                case "template":
                    return new DocumentPolicy
                    {
                        Mode = mode,
                        Rules = new[]
                        {
                            "This is a template file (.rte) opened for editing; do not overwrite it.",
                            "To start a project from it, save it with revit_save_document(save_as_path) to a new local .rvt path the user gave; never choose the path yourself.",
                            "Saving the template itself is left to the user in Revit."
                        },
                        Allowed = new[] { "revit_get_document_state", "revit_save_document" }
                    };
                default:
                    throw new ArgumentException("Unknown document mode.", nameof(mode));
            }
        }
    }
}
