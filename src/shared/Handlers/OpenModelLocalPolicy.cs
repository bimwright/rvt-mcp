using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// The file-safety rules for open_model's Create New Local flow (design
    /// doc §2.5) plus the path normalization used when matching documents to
    /// paths on disk. Kept free of Revit API references so the test assembly
    /// can compile this file and exercise the rules directly.
    /// </summary>
    public static class OpenModelLocalPolicy
    {
        public enum CollisionDecision
        {
            /// Target name is free - go straight to CreateNewLocal.
            CreateLocal,
            /// Old local verified safe to move - rename with a timestamp, then create.
            RenameAndCreate,
            /// A document in this session (model or loaded link) has the target
            /// path open - leave it untouched and report (rule §2.5).
            StopOpenInSession,
            /// Header proves changes not yet saved to central - stop and ask
            /// the user, never auto-rename (rule §2.5).
            StopUnsynced,
            /// Header unreadable - sync state cannot be proven, so the file is
            /// left untouched and reported rather than moved on a guess.
            StopUnverifiable
        }

        /// <summary>
        /// Order matters: an open file is never touched regardless of what its
        /// header says, and a file whose sync state cannot be proven is treated
        /// as protected rather than movable.
        /// </summary>
        public static CollisionDecision Decide(
            bool targetExists,
            bool targetOpenInSession,
            bool headerRead,
            bool isWorkshared,
            bool allLocalChangesSavedToCentral)
        {
            if (!targetExists) return CollisionDecision.CreateLocal;
            if (targetOpenInSession) return CollisionDecision.StopOpenInSession;
            if (!headerRead) return CollisionDecision.StopUnverifiable;
            if (isWorkshared && !allLocalChangesSavedToCentral) return CollisionDecision.StopUnsynced;
            return CollisionDecision.RenameAndCreate;
        }

        /// <summary>
        /// Ordinal-ignore-case equality over normalized paths. Two strings that
        /// spell the same file differently - slash style, casing, trailing
        /// slash, "..", or a mapped drive vs its UNC root - compare equal.
        /// </summary>
        public static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves "." and ".." via GetFullPath and expands a mapped drive to
        /// its UNC root (design doc §3: a central stored as a UNC path must
        /// still match a document opened through the mapped letter). Anything
        /// unparseable - including unsupported schemes like RSN:// - is
        /// returned in minimally normalized form so comparisons stay stable.
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            var p = path.Trim().Trim('"');
            try
            {
                // GetFullPath only makes sense on rooted filesystem paths; a
                // relative or scheme path (RSN://...) must not be glued onto
                // the current directory.
                if (Path.IsPathRooted(p))
                {
                    p = Path.GetFullPath(p);
                    var unc = TryExpandMappedDrive(p);
                    if (unc != null) p = unc;
                }
            }
            catch { /* leave the path as typed - comparisons just won't equate it */ }
            return p.Replace('/', '\\').TrimEnd('\\');
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

        /// <summary>"K:\dir\file" -> "\\server\share\dir\file" when K: is a mapped drive.</summary>
        private static string TryExpandMappedDrive(string path)
        {
            if (path.Length < 3 || path[1] != ':' || !char.IsLetter(path[0]) || path[2] != '\\')
                return null;
            var remote = new StringBuilder(1024);
            var length = remote.Capacity;
            if (WNetGetConnection(path.Substring(0, 2), remote, ref length) != 0)
                return null;
            var unc = remote.ToString().TrimEnd('\\');
            return string.IsNullOrEmpty(unc) ? null : unc + path.Substring(2);
        }
    }
}
