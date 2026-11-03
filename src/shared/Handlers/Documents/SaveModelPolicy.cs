using System;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Path rules behind revit_save_model. No Revit types, so they are unit-tested without Revit.
    /// </summary>
    public static class SaveModelPolicy
    {
        /// <summary>
        /// Null when <paramref name="target"/> is an absolute .rvt path on a local drive; otherwise the
        /// reason it is refused. Network paths are refused, including a mapped drive letter.
        /// </summary>
        public static string CheckSaveAsPath(string target)
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

        /// <summary>True when the document has no project file to save in place (new, or opened from a template).</summary>
        public static bool NeedsSaveAsPath(string currentPath)
            => string.IsNullOrEmpty(currentPath) || currentPath.EndsWith(".rte", StringComparison.OrdinalIgnoreCase);
    }
}
