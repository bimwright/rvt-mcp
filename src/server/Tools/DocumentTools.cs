using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("documents")]
    public class DocumentTools
    {
        [McpServerTool(Name = "revit_save_document", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
            "Save the confirmed active document. This overwrites the working file; Revit keeps a backup. Older formats require explicit user approval and allow_format_upgrade=true. A timeout may leave the operation running; inspect its status before retrying.")]
        public static async Task<string> SaveDocument(bool allow_format_upgrade = false, int timeout_seconds = 600)
        {
            var blocked = ServerState.BlockIfReadOnly("save_document");
            if (blocked != null) return blocked;
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;
            return await ToolGateway.CallAsync("save_document", new { allow_format_upgrade }, timeout_seconds);
        }

        [McpServerTool(Name = "revit_save_checkpoint", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
            "With user approval, create a checkpoint of a local model or family. It may first save and overwrite the working file; Revit keeps a backup. A timeout may leave the operation running; inspect its status before retrying.")]
        public static async Task<string> SaveCheckpoint(
            [Description("Checkpoint label, 1-80 characters.")] string label,
            [Description("Optional reason, up to 500 characters.")] string reason = null,
            bool milestone = false, bool allow_format_upgrade = false, int timeout_seconds = 600)
        {
            var blocked = ServerState.BlockIfReadOnly("save_checkpoint");
            if (blocked != null) return blocked;
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;
            return await ToolGateway.CallAsync("save_checkpoint", new { label, reason, milestone, allow_format_upgrade }, timeout_seconds);
        }

        [McpServerTool(Name = "revit_manage_checkpoints", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
            "List, prune, or restore checkpoints of the confirmed active local model or family. Prune cannot be undone: show a dry-run plan and apply its plan_hash only after user approval. Restore creates a new file; the working model is not overwritten. A timeout may leave a restore copying; inspect its status before retrying.")]
        public static async Task<string> ManageCheckpoints(
            [Description("list, prune, or restore.")] string action,
            [Description("Non-milestone snapshots to retain when pruning, 1-100.")] int keep_last = 5,
            [Description("For prune, preview without deleting (default true).")] bool dry_run = true,
            [Description("Hash of the user-approved prune plan, required when applying.")] string plan_hash = null,
            [Description("Indexed checkpoint to restore.")] string checkpoint_id = null,
            int timeout_seconds = 600)
        {
            var blocked = ServerState.BlockIfReadOnly("manage_checkpoints");
            if (blocked != null) return blocked;
            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;
            return await ToolGateway.CallAsync("manage_checkpoints", new { action, keep_last, dry_run, plan_hash, checkpoint_id }, timeout_seconds);
        }
    }
}
