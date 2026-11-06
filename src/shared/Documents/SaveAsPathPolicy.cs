using System;
using RvtMcp.Plugin.Handlers;

namespace RvtMcp.Plugin.Documents
{
    /// <summary>
    /// Rules for revit_save_document(save_as_path): the first save of a document that has no project
    /// file yet. No Revit types, so they are unit-tested without Revit. Path rules from PR #29.
    /// </summary>
    public static class SaveAsPathPolicy
    {
        /// <summary>
        /// Only a document without its own project file may be saved to a new path. A saved model
        /// already has a working file; copies of it are checkpoints.
        /// </summary>
        public static string CheckMode(string mode)
            => mode == "unsaved" || mode == "template" ? null : "SAVE_AS_NOT_ALLOWED";

        /// <summary>
        /// Null when <paramref name="target"/> is an absolute .rvt path on a local drive; otherwise the
        /// reason it is refused. Network paths are refused, including a mapped drive letter. Reasons
        /// never quote the path, because error text is redacted before it reaches the client.
        /// </summary>
        public static string CheckPath(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return "save_as_path is empty.";
            if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal))
                return "save_as_path must be on a local drive; network (UNC) paths are not written.";
            // Path.IsPathRooted also accepts "\file.rvt" and "C:file.rvt", which resolve against the
            // Revit process's current drive or folder.
            if (target.Length < 3 || !char.IsLetter(target[0]) || target[1] != ':' || (target[2] != '\\' && target[2] != '/'))
                return "save_as_path must be a full local path that starts with a drive letter.";
            if (!target.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                return "save_as_path must name a Revit project file (rvt extension).";
            if (OpenModelLocalPolicy.NormalizePath(target).StartsWith(@"\\", StringComparison.Ordinal))
                return "save_as_path is on a mapped network drive; network paths are not written.";
            return null;
        }
    }
}
