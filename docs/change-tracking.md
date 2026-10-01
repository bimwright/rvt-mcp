# Element changes during MCP commands

Development source includes `_changes` metadata when Revit reports committed element
changes during a synchronous MCP command. It needs a server and plugin built with
this feature. Existing 1.0.0 candidate bundles do not acquire it automatically.

The metadata covers typed tools, `send_code_to_revit`, `batch_execute` and baked tools
through the common command dispatcher. It is not a new tool and does not grant write
permission. Events outside the command scope, including subsequent manual Undo, are
not captured. Ordinary reads acquire neither `_changes` nor `_history` and publish
no history files, including in read-only mode.

```json
{"_changes":{"complete":true,"documents":[{
  "document":"Sample","status":"complete","transactions":["MCP: Set parameters"],
  "added":{"count":0,"ids":[]},
  "modified":{"count":1,"ids":[606069]},
  "deleted":{"count":0,"ids":[]},
  "by_category":{"Structural Columns":1},"truncated":false
}]}}
```

Counts use deduplicated 64-bit IDs per document. Add-then-modify stays added;
add-then-delete within the call disappears. Modification events are not a parameter
diff: setting a value back can still report a modification. Internal and indirect
elements are included. Deleted elements can have category `unknown`; the plugin does
not scan the whole model to recover deleted metadata. Document titles can be identical:
do not merge separate document entries by title or compare IDs across documents.

Each kind returns at most 200 sorted IDs. A complete capture with `truncated=true`
still has exact counts, but not necessarily every ID or transaction name. More than
50,000 tracked elements in a document, more than eight documents, or capture errors
produce incomplete coverage. The response guard can replace details with an explicit
omission notice. Never interpret missing IDs or null counts as zero changes.

Transaction rollback has no accepted changes. A confirmed full rollback from
`batch_execute` produces no `_changes`. A group rollback/undo inside arbitrary code
cannot reliably identify the surviving commits from event IDs alone, so that document
reports `status=incomplete`, null counts and empty ID lists. A command that commits
and then fails still returns captured metadata with its error; inspect before retrying.
Timeouts remain unknown outcomes and may prevent delivery of this metadata.

The server uses an allowlist for agent-facing change metadata. With call logging
enabled, the journal adds a separate `Changes` field, not subject to the 2,048-character
`Result` text truncation. Call logging remains off by default. The separate local
history feature below is enabled by default in development builds.

The `revit_change` prompt uses available metadata to compare the agreed scope, while
still requiring readback of actual values. No metadata from an older plugin is not
evidence that nothing changed.

## Local model history and reasons (development)

The server stores SQLite history under `%LOCALAPPDATA%\Bimwright\rvt-mcp\projects`.
This is independent of the call log: `enableCallLog=false` does not turn it off.
To disable recording, set `enableChangeHistory=false` in `rvtmcp.config.json`, set
`BIMWRIGHT_ENABLE_CHANGE_HISTORY=false`, or pass `--disable-change-history`.
`--enable-change-history` enables it; CLI overrides environment, which overrides JSON.
Existing history remains queryable when recording is off. Disabling does not delete it.

Each model has its own database, named by an opaque SHA-256 key. Workshared models
use central identity; ordinary models use their file path; cloud models use project
and model GUIDs. Names/titles are never identity. Path aliases are not resolved to
one physical file. Unsaved, detached and unresolved identities are reported as
unavailable instead of being merged into another model's history.

Private model identities/paths stay local. The plugin transfers a separate private
capture to the server; only the server writes SQLite (WAL and a busy timeout).
Every tracked ID is stored independently of the public 200-ID summary. The existing
50,000-element/eight-document bounds and incomplete-capture rules still apply.
Incomplete final sets have no alleged final element IDs. Deleted UniqueIds may be
unavailable. Read-only commands do not create change-call rows.

The `_history` receipt contains `status`, a server-issued `callId` when changes were
captured, the active `modelKey`, and changed `models` with their own keys. Preserve
these IDs before readback calls. A missing/failed receipt is not proof of recording.
`partial`, `skipped_identity`, `unavailable` and `storage_failed` are separate from the model operation's
success: the model may already have changed. Never replay it to obtain history.

Only commands with observed changes publish private captures. Documents without a
stable identity are skipped before file creation; mixed captures record known models
and report `partial` with `skippedDocuments`. No-identity captures report
`skipped_identity` without creating a pending file.

Private captures that reached disk survive a server/database failure. An enabled,
non-read-only server recovers them idempotently in a background worker, starting after
two seconds and repeating about once a minute. Initialization does not wait for recovery.
Each pass visits at most 16 files and reads at most 256 MiB, with a five-second budget
checked between files; a single file operation can take longer. Calls still awaiting
their gateway response are left alone. Invalid, unresolved legacy, oversized or older
than seven-day captures move to `history-quarantine`, outside the retry queue. Quarantine
preserves evidence for manual inspection and is not automatically replayed or deleted.
Transient storage failures stay pending for retry. A failure before a capture reaches disk cannot be recovered by
this mechanism. History does not confirm Save/Sync and is not updated by subsequent
manual Undo or model edits outside MCP.

Two `meta` tools use the opaque model key; neither accepts a model path:

- `revit_record_change(modelKey, callIds, requestText, goal, reason, ...)` attaches a
  reason to an explicit JSON string array of server-issued call IDs. All must belong
  to the selected model and be unassigned; validation/assignment is atomic. This
  changes only local history and is hidden in read-only mode. Empty reason is stored
  as unknown. Optional `alternativesJson`, `surveyJson` and `remainingWorkJson` accept
  JSON objects/arrays. Text is redacted, limited to 32,768 input characters overall
  and 4,096 characters per string; truncation is explicit.
- `revit_get_change_records(modelKey?, elementId?, uniqueId?, from?, until?, limit=50)`
  returns local calls and their reasons. Dates are interpreted in UTC; limit is 1–100.
  Omit `modelKey` to resolve the active saved model without writing history files.
  Supplying an existing key also works without a Revit connection.
  `reasonStatus` distinguishes `unassigned`, `unknown` and `recorded`. Each returned
  call shows at most 200 elements; filter by a specific ID to query others in the DB.
  `hasMore` indicates more matching calls. Missing rows do not prove no change.

Before/after values currently come from committed `updated` rows of the typed
`set_element_parameter_values` handler (including partial-success calls): raw values and display strings are stored
only for elements actually observed as modified. They are redacted and bounded;
long values carry `truncated=true`. Other commands have null before/after data.
The reason tool cannot supply or override this evidence, and no arbitrary code body
or general tool-argument payload is stored by change history.

These are development capabilities. Previously built Setup/MCPB bundles and installed
clients do not gain them automatically; a matching new plugin/server pair is required.
