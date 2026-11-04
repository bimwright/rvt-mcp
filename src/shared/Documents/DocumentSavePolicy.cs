using System;

namespace RvtMcp.Plugin.Documents
{
    public static class DocumentSavePolicy
    {
        // Null means proceed; "no_changes" is a successful no-op for save_document.
        public static string Check(string mode, bool fileExists, string versionStatus, bool isModified,
            bool upgradeOnSave, bool allowFormatUpgrade, bool isReadOnly, bool checkpoint)
        {
            if (mode == null) return "WORKSHARED_CENTRAL_UNKNOWN";
            if (checkpoint && (mode == "workshared_local" || mode == "workshared_central"))
                return "CHECKPOINT_WORKSHARED_UNSUPPORTED";
            switch (mode)
            {
                case "cloud": return "CLOUD_MODEL_UNSUPPORTED";
                case "unsaved": return "DOCUMENT_NEVER_SAVED";
                case "workshared_central": return "CENTRAL_OPEN_DIRECTLY";
                case "local_file":
                case "family":
                case "workshared_local": break;
                default: throw new ArgumentException("Unknown document mode.", nameof(mode));
            }
            if (!fileExists) return "FILE_MISSING_ON_DISK";
            if (versionStatus == "changed_on_disk") return "FILE_CHANGED_ON_DISK";
            if (!isModified) return checkpoint ? null : "no_changes";
            if (upgradeOnSave && !allowFormatUpgrade) return "FORMAT_UPGRADE_NEEDS_USER";
            if (isReadOnly) return "FILE_READ_ONLY";
            return null;
        }
    }
}
