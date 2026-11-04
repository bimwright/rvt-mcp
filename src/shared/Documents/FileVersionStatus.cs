using System;

namespace RvtMcp.Plugin.Documents
{
    public static class FileVersionStatus
    {
        public static string Compare(int memorySaves, Guid memoryGuid, int? diskSaves, Guid? diskGuid)
        {
            if (!diskSaves.HasValue) return "missing_on_disk";
            if (!diskGuid.HasValue) throw new ArgumentException("Disk version GUID is required when the file exists.", nameof(diskGuid));
            if (diskSaves.Value != memorySaves) return "changed_on_disk";
            // Equal save counts are in sync whatever the GUIDs say: Revit changes the in-memory
            // GUID on every edit and when it opens an older format (SPEC r7, P4 F1).
            return "in_sync";
        }
    }
}
