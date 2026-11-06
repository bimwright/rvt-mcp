using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("documents")]
    public class DocumentTools
    {
        [McpServerTool(Name = "revit_save_document", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
            "Save the confirmed active document after verified work, following revit_get_document_state policy. This overwrites the working file; Revit keeps a backup. Older formats require explicit user approval and allow_format_upgrade=true. " +
            "A document with no project file yet (unsaved, or a .rte template opened for editing) is saved only with save_as_path, a new local .rvt the user named; existing files are never overwritten. " +
            "A timeout may leave the operation running; inspect its status before retrying. Example: revit_save_document(allow_format_upgrade=false, timeout_seconds=600).")]
        public static async Task<string> SaveDocument(
            [Description("Allow saving in the host's newer Revit format only after explicit user approval (default false).")] bool allow_format_upgrade = false,
            [Description("Seconds to wait, 1-900 (default 600); inspect operation status after a timeout before retrying.")] int timeout_seconds = 600,
            [Description("First save only: absolute local .rvt path the user gave, for an unsaved document or an opened .rte template. Omit for a normal save.")] string save_as_path = null)
        {
            var blocked = ServerState.BlockIfReadOnly("save_document");
            if (blocked != null) return blocked;
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;
            return await ToolGateway.CallAsync("save_document", new { allow_format_upgrade, save_as_path }, timeout_seconds);
        }

        [McpServerTool(Name = "revit_save_checkpoint", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
            "With user approval, create a checkpoint of a local model or family before risky work or at handover. It may first save and overwrite the working file; Revit keeps a backup. A timeout may leave the operation running; inspect its status before retrying. Example: revit_save_checkpoint(label=\"Before renumbering sheets\", reason=\"User approved the sheet plan\", milestone=true).")]
        public static async Task<string> SaveCheckpoint(
            [Description("Checkpoint label, 1-80 characters.")] string label,
            [Description("Optional reason, up to 500 characters.")] string reason = null,
            [Description("Keep this checkpoint during pruning without counting it toward keep_last (default false).")] bool milestone = false,
            [Description("Allow saving in the host's newer Revit format only after explicit user approval (default false).")] bool allow_format_upgrade = false,
            [Description("Seconds to wait, 1-900 (default 600); inspect operation status after a timeout before retrying.")] int timeout_seconds = 600)
        {
            var blocked = ServerState.BlockIfReadOnly("save_checkpoint");
            if (blocked != null) return blocked;
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;
            return await ToolGateway.CallAsync("save_checkpoint", new { label, reason, milestone, allow_format_upgrade }, timeout_seconds);
        }

        [McpServerTool(Name = "revit_manage_checkpoints", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
            "List checkpoints to inspect them, prune to free space, or restore to inspect an earlier state of the confirmed active local model or family. Prune cannot be undone: show a dry-run plan and apply its plan_hash only after user approval. Restore creates a new file; the working model is not overwritten. A timeout may leave a restore copying; inspect its status before retrying. Example: revit_manage_checkpoints(action=\"prune\", keep_last=5, dry_run=true).")]
        public static async Task<string> ManageCheckpoints(
            [Description("list, prune, or restore.")] string action,
            [Description("Non-milestone snapshots to retain when pruning, 1-100.")] int keep_last = 5,
            [Description("For prune, preview without deleting (default true).")] bool dry_run = true,
            [Description("Hash of the user-approved prune plan, required when applying.")] string plan_hash = null,
            [Description("Indexed checkpoint to restore.")] string checkpoint_id = null,
            [Description("Seconds to wait, 1-900 (default 600); inspect operation status after a timeout before retrying.")] int timeout_seconds = 600)
        {
            var blocked = ServerState.BlockIfReadOnly("manage_checkpoints");
            if (blocked != null) return blocked;
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;
            return await ToolGateway.CallAsync("manage_checkpoints", new { action, keep_last, dry_run, plan_hash, checkpoint_id }, timeout_seconds);
        }
    }
}
