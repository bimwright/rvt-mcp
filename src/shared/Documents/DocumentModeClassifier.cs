using System;
using RvtMcp.Plugin.Handlers;

namespace RvtMcp.Plugin.Documents
{
    public static class DocumentModeClassifier
    {
        public static string Classify(bool isCloud, bool isFamily, bool isWorkshared, string path, string centralPath)
        {
            if (isCloud) return "cloud";
            if (string.IsNullOrWhiteSpace(path)) return "unsaved";
            if (isFamily) return "family";
            // revit_open_model opens a .rte for editing; an in-place save would overwrite the template itself.
            if (path.EndsWith(".rte", StringComparison.OrdinalIgnoreCase)) return "template";
            if (!isWorkshared) return "local_file";
            if (string.IsNullOrWhiteSpace(centralPath))
                throw new ArgumentException("A central path is required for a workshared document.", nameof(centralPath));
            return OpenModelLocalPolicy.PathsEqual(path, centralPath) ? "workshared_central" : "workshared_local";
        }

        /// <summary>
        /// True when the file at <paramref name="path"/> is the central model itself.
        /// A local made by CreateNewLocal keeps IsCentral=true in its file header
        /// until its first save, so the header flag alone cannot tell it from the
        /// central; the central path it stores can. With no central path known,
        /// the header flag is kept, so an unclassifiable file is never treated as
        /// less risky than its header says.
        /// </summary>
        public static bool IsCentralFile(bool isWorkshared, bool headerIsCentral, string path, string centralPath)
        {
            if (!isWorkshared) return false;
            if (string.IsNullOrWhiteSpace(centralPath)) return headerIsCentral;
            return OpenModelLocalPolicy.PathsEqual(path, centralPath);
        }
    }
}
