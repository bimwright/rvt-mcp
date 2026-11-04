# Document lifecycle: save and checkpoints

This guide describes the current development source. The published v1.2.2 release does not include these four tools.

Use `revit_get_document_state` before saving and follow the returned `policy`. A save overwrites the working file; create a checkpoint only before risky work, with the user's approval, or at handover. Ask the user before pruning or restoring a checkpoint. At the end of the work, report any changes that remain unsaved or, for a workshared local file, unsynchronized.

## Tools and availability

| Tool | Toolset | Effect |
| --- | --- | --- |
| `revit_get_document_state` | `meta` | Read the active document or all open documents and the active document's policy. |
| `revit_save_document` | `documents` | Save the active document, overwriting its working file. |
| `revit_save_checkpoint` | `documents` | Save the active document if modified, then make a verified file copy and add it to the checkpoint index. |
| `revit_manage_checkpoints` | `documents` | List, prune or restore checkpoints of the active document. |

The `documents` toolset is off in bare server defaults. Enable `--toolsets all`, or include both `meta` and `documents` in an explicit toolset list; selecting `documents` alone does not include the state tool. An installed configuration may already select `all`. `--read-only` keeps the state tool when `meta` is enabled, but removes all three `documents` tools, including the list action. The three `documents` commands must be called directly: `revit_batch_execute` refuses them, including `manage_checkpoints` with `action="list"`.

`revit_save_document` and `revit_save_checkpoint` take `allow_format_upgrade` (default `false`) and `timeout_seconds` (1–900, default 600). `revit_manage_checkpoints` takes the same `timeout_seconds`, because a restore copies the whole model file. A caller timeout can occur while Revit is still processing the operation. Inspect its operation receipt and `revit_get_operation_status` before deciding what to do; do not blindly retry a save.

## Inspecting a document

`scope="active"` is the default. `scope="all"` lists the documents open in the selected Revit instance, excluding documents loaded only as links. At Revit Home, an empty `documents` array is a valid result. The top-level `policy`, when available, applies to the active document.

Each document normally includes its title, path, `document_id`, active/family/modified flags, mode, saved format, host year, upgrade and read-only state, file size and free space. `file_version` compares the in-memory and on-disk `number_of_saves`. Both `version_guid` values are returned for reference only: Revit changes the in-memory GUID on every edit and when it opens an older file format, so a GUID difference is normal.

| Status | Meaning |
| --- | --- |
| `in_sync` | The save counts match, whether or not the GUIDs do. |
| `changed_on_disk` | The disk save count differs from memory in **either direction**, including replacement by an older file. Save and checkpoint are blocked. |
| `missing_on_disk` | The file with a local path is no longer present. |

Unavailable observations remain unknown rather than being guessed. Check the document's `warnings`, `mode_error` and checkpoint status. If the checkpoint index mutex is busy, that document has `checkpoints.index_status="busy"`, with unknown summary values; other documents still appear. Other per-document read failures are returned as an item with `error="DOCUMENT_STATE_UNAVAILABLE"` and best-effort `title`, `path` and `is_active`; these fields can be `null` when they could not be established. Inspect that item rather than assuming its state. An unreadable index has `index_status="unreadable"`; an index not yet created has `index_status="not_created"`.

## Modes and save policy

| Mode | Save | Checkpoint / manage |
| --- | --- | --- |
| `local_file` | Save a non-workshared `.rvt` or `.rte` at its existing path. | Supported. |
| `family` | Save a family at its existing path. | Supported. |
| `workshared_local` | Save only the local file. Sync to central is not supported; tell the user about unsynchronized changes. | Not supported. |
| `workshared_central` | Do not save a central opened directly. Ask the user to reopen a local copy; `revit_open_model` creates a local for a workshared input. | Not supported. |
| `cloud` | Not supported; ask the user to save in Revit. | Not supported. |
| `unsaved` | No path has been chosen; ask the user to use Save As in Revit. | Not supported. |

If a workshared document's central path cannot be read, the state returns `mode=null` and `mode_error="WORKSHARED_CENTRAL_UNKNOWN"`; save and checkpoint refuse rather than guessing the mode.

For `revit_save_document`, checks stop in this order: an active document and an allowed mode; file exists; disk save count has not changed; if unmodified, return `saved=false, reason="no_changes"`; then check format upgrade and file ReadOnly. An unmodified file therefore returns `no_changes` before the upgrade and ReadOnly checks. A modified file in an older format requires the user's explicit approval and `allow_format_upgrade=true`; a modified file whose format cannot be determined is refused. The tool never clears ReadOnly on the user's working file.

Revit's existing native-action guard still applies. If a save requires a user decision, inspect `_failure` and the Issues record; a user can grant the native action once in History → Issues, after which the agent retries the **same** command. Saving a workshared local, including the first save of a newly created local, needs no such grant unless the save would upgrade the file format; saving a central opened directly still does. This is separate from approval to upgrade a file format. See [controlled failures](controlled-failures.md).

Reload Latest from Revit's UI can advance a workshared local's memory save count while its saved file remains unchanged. Save then returns `FILE_CHANGED_ON_DISK`. If this followed a UI Reload, ask the user to review the open local and **Save it in Revit**, without Synchronize with Central. Then call `revit_get_document_state(scope="active")` again and confirm the intended document and file version before retrying. The agent must not bypass this check with direct API Save, overwrite the local, or repeat Reload/Sync to try to clear the error. If a UI Reload is not established, first investigate a possible external replacement of the file; the same error does not prove UI Reload occurred. Automatic recognition of UI Reload is not implemented.

## Revit backup limit

For a local file or family, the target is **at least 20** Revit backups, not a maximum of 20. `backups.min_backups_ensured` is `20` when recorded, otherwise `null`; it is a minimum known at the time of the record, not a reading of the file's actual backup setting. `ensured_by` is `raised`, `observed`, or `null`; `count_on_disk` is the current count of matching `<model>.NNNN.<ext>` files.

- An existing registry record uses normal `Document.Save()`.
- Without a record, at least 20 matching backups demonstrate a limit of at least 20: use normal Save, retain the existing setting, and record `ensured_by="observed"` with `raised_now=false`.
- Without a record and with fewer than 20 matching backups, SaveAs to the **same path** with `MaximumBackups=20`; after the save, record `ensured_by="raised"` and return `raised_now=true`.

The per-user registry is `%LOCALAPPDATA%\Bimwright\rvt-mcp\documents\backup-limits.json`, keyed by normalized full path. An unreadable or invalid registry is treated as empty; a successful save can proceed even if a later registry write fails or times out, with a warning. A later save may raise the limit again. The registry cannot detect a subsequent manual change of the Revit setting. Since Revit's current limit cannot be read, an unregistered file configured above 20 but with fewer than 20 backups will be set to 20; an unrelated file matching the backup naming pattern can also be counted. A workshared local save does not use this local-file backup policy and does not sync central.

## Creating a checkpoint

Call `revit_save_checkpoint` on an active local model, template or family with a nonblank `label` of at most 80 characters. Optional `reason` is at most 500 characters, and `milestone` defaults to `false`. The same active-mode, file existence and disk-version checks apply. Format upgrade and ReadOnly checks apply when the document is modified and must be saved first. The tool also checks the index and requires free space of at least 1.1 times the model file's size.

If modified, Revit first saves the working file using the backup rules above. The tool then copies it without overwriting into `<model-folder>\<Model>.checkpoints\<yyyyMMdd-HHmmss>[-n].<rvt|rfa|rte>`, clears ReadOnly on its new copy, verifies size and the disk `NumberOfSaves`/`VersionGUID`, and atomically updates `index.json`. The ID uses local clock time; stored `created_utc` values determine newest-first ordering. Duplicate names gain `-2`, `-3`, and so on. A successful response includes `checkpoint`, `saved_first`, totals and, when there are more than five non-milestones, a `prune_hint`. The hint never deletes anything automatically.

**A failed checkpoint does not mean the working file was left unsaved.** If the failure happened after the initial Save, its `data` reports `saved_first=true`; the working file has already been overwritten. `checkpoint_path` identifies the attempted copy when known, and `checkpoint_copy_removed` reports cleanup when attempted (`true`, `false`, or `null` when not reported/attempted). Inspect these fields and the working document before choosing a next action; a failed verification or index write attempts to remove the copy, but cleanup itself can fail.

## List, prune and restore

`revit_manage_checkpoints(action="list")` returns indexed entries newest first, each with `exists` and current `bytes`, plus `untracked_files`. Files not listed in the index are reported but never automatically deleted.

For pruning, first call with `action="prune"` and `dry_run=true` (the default). `keep_last` is 1–100, default 5. All milestones are kept, as are the newest `keep_last` non-milestones; missing indexed files are candidates for removal from the index only. The plan includes the files to delete, bytes to free and `plan_hash`. Show the plan to the user: **deletion cannot be undone**. After approval, apply that exact plan with `dry_run=false` and its `plan_hash`. The tool recalculates and refuses a changed plan, and only deletes indexed files within the checkpoints directory. Review `deleted`, `missing_removed` and `failed`. If index replacement fails after some deletions, the response can report deleted files while the old index still contains their entries; do not assume the operation was all-or-nothing.

For restore, use `action="restore"` with an indexed `checkpoint_id`. A new file `<Model>-restored-<id>.<ext>` is created next to the working model; an existing target is never overwritten, and ReadOnly is cleared on the new file. The tool neither opens the copy nor modifies the working file. The user can open the copy with `revit_open_model`. Before writing, verify it with `revit_switch_target(verify=true, expectedDocumentId=...)`, using `_target.document_id` from the open result; without `expectedDocumentId` the same session receives `DOCUMENT_CHANGED`. To replace the original, the user must close it in Revit and rename files manually. The restored file has a new model scope and does not inherit the original's rules, grants or history.

The checkpoints folder and `index.json` are created only for the first checkpoint. Index reads and read–modify–write updates use a path-specific `Local\` named mutex with a wait of about 10 seconds. It coordinates processes in the **same Windows session only**; two machines writing the same checkpoint index on a network share are not protected by this mutex.

## Errors and warnings

The following errors are in addition to ordinary argument validation and the shared gateway/failure receipts. Inspect the response as a whole, especially after an operation that may already have saved or deleted files.

| Error | Meaning |
| --- | --- |
| `NO_ACTIVE_DOCUMENT` | Save, checkpoint or manage was requested with no active document. |
| `DOCUMENT_STATE_UNAVAILABLE` | A state-read item could not be described; other document items remain available. |
| `WORKSHARED_CENTRAL_UNKNOWN` | The central path could not be read; no mode is guessed. |
| `CENTRAL_OPEN_DIRECTLY` | Saving a directly opened central is refused. |
| `CLOUD_MODEL_UNSUPPORTED` | The requested write is not supported for a cloud model. |
| `DOCUMENT_NEVER_SAVED` | The document has no path. Save As in Revit. |
| `FILE_MISSING_ON_DISK` | The working file is no longer present. |
| `FILE_CHANGED_ON_DISK` | Disk and memory save counts differ. If this followed UI Reload on a workshared local, ask the user to review and Save local in Revit, then re-read state before retrying. Otherwise inspect possible external replacement; do not overwrite automatically. |
| `FORMAT_UPGRADE_NEEDS_USER` | The file is older and explicit upgrade approval was not supplied. |
| `FILE_FORMAT_UNKNOWN` | A modified document's file format could not be determined; the tool does not assume an upgrade is unnecessary. |
| `FILE_READ_ONLY` | The working file is ReadOnly and must not be changed by the tool. |
| `CHECKPOINT_WORKSHARED_UNSUPPORTED` | Checkpoint operations do not support workshared local or central models. |
| `CHECKPOINT_UNSUPPORTED` | The active document is not a supported checkpoint mode. |
| `INSUFFICIENT_DISK_SPACE` | Available space is less than 1.1 times the source size. |
| `FREE_SPACE_UNAVAILABLE` | Checkpoint could not determine free space. |
| `DOCUMENT_CHANGED` | The active document changed between Save and copying a checkpoint. Check `saved_first`. |
| `FILE_VERSION_UNAVAILABLE` | Checkpoint could not read the source file version after Save. Check `saved_first`. |
| `CHECKPOINT_INDEX_UNREADABLE` | The checkpoint index or its directory cannot be read or is invalid; it is not overwritten. |
| `CHECKPOINT_INDEX_BUSY` | A checkpoint operation could not acquire the index mutex in time. State reads instead report `index_status="busy"` for that document. |
| `CHECKPOINT_COPY_FAILED` | The checkpoint directory or copy could not be created, or no free name was found. |
| `CHECKPOINT_VERIFY_FAILED` | The copied file did not verify against the source; cleanup is attempted. |
| `CHECKPOINT_INDEX_WRITE_FAILED` | Index update failed; for checkpoint creation cleanup of the new copy is attempted. A prune can already have deleted files. |
| `PLAN_HASH_REQUIRED` | Applying a prune requires the hash of the approved dry-run plan. |
| `PLAN_CHANGED` | The current prune plan no longer matches the approved hash; obtain and review a new plan. |
| `CHECKPOINT_ID_REQUIRED` | Restore requires a checkpoint ID. |
| `CHECKPOINT_NOT_FOUND` | The requested ID is not in the index. |
| `CHECKPOINT_FILE_MISSING` | The indexed snapshot file is missing. |
| `RESTORE_TARGET_EXISTS` | The restore target already exists and was not overwritten. |
| `CHECKPOINT_IO_FAILED` | Another checkpoint file read/write failed during management. |
| `REVIT_OPERATION_REQUIRES_USER` | A native Revit action needs a user decision; inspect `_failure` and Issues. |

Warnings do not themselves mean that a successfully reported operation failed:

| Warning | Meaning |
| --- | --- |
| `FILE_FORMAT_UNKNOWN` | State read could not determine whether the file format needs an upgrade. |
| `FILE_VERSION_UNAVAILABLE` | State read could not obtain file version details. |
| `MEMORY_VERSION_UNAVAILABLE` | State read could not obtain the in-memory version. |
| `FILE_ATTRIBUTES_UNAVAILABLE` | State read could not obtain file attributes. |
| `FILE_SIZE_UNAVAILABLE` | State read could not obtain file size. |
| `FREE_SPACE_UNAVAILABLE` | State read could not obtain available space. |
| `BACKUP_COUNT_UNAVAILABLE` | The matching backup count could not be read. |
| `BACKUP_REGISTRY_BUSY` | Save completed, but the backup registry mutex timed out; a later save may raise the limit again. |
| `BACKUP_REGISTRY_WRITE_FAILED` | Save completed, but the backup registry could not be written. |
| `SAVED_VERSION_UNAVAILABLE` | Save completed, but the resulting save count could not be read. |
| `ACTIVE_DOCUMENT_CHANGED` | The active document changed after the save; verify the intended target before further work. |
