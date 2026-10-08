# Controlled failures and Revit Issues

This guide describes **unreleased development behavior**. It is not a published release or a completed native acceptance report. Candidate checks on Revit 2022, 2024 and 2027 are pending; Revit 2025/.NET 8 live acceptance has not run. Build coverage alone does not establish native behavior on every supported year.

## What changes

During an MCP edit, an unapproved Revit **Warning now requests rollback**, as do Error, DocumentCorruption and incomplete failure capture. Earlier versions of the shared helper deleted warnings and continued. The helper and native failure event now use the same policy so warnings are recorded before their state changes. Outside an MCP operation, normal Revit user actions retain their native behavior.

Warnings observed while natively opening or upgrading an existing model are recorded as opening context rather than attributed to a new agent edit. That exception does not accept Error, Corruption or uncertain capture: cancellation is attempted where the API permits, and the response reports the actual opening outcome.

A failure response includes `_failure`: the operation and incident IDs, model scope, action, mutation outcome, gate state, persistence state and bounded captured messages. `_operation` describes execution that is pending, interrupted or completed later. Read these fields even when custom code returned its own success value. A warning accepted by a user is still a warning and does not certify design quality.

Rollback applies to the current supported transaction. A policy stop also prevents subsequent supported commits in the same call. `batch_execute` stops and rolls back its outer group even with `continueOnError=true`. Arbitrary code that already committed an earlier transaction can leave changes behind; such an outcome can be `partial` or `unknown`. External side effects are not undone by Revit rollback.

## Model gates and local records

An unresolved incident, uncertain operation outcome or storage/capture failure blocks further MCP writes for the affected model. Supported reads remain available when Revit's UI thread is free. Switching clients or restarting the gateway does not clear the gate. Durable saved-model state is retained across Revit restart; an unsaved or unresolved document may have only a live-session identity.

The failure ledger lives locally at `%LOCALAPPDATA%\Bimwright\rvt-mcp\failures\ledger.db`. It is separate from change history: disabling call logs or `_history` recording does not disable failure handling or erase rollback incidents. The add-in writes the ledger, so Issues remains usable when the gateway is stopped. The gateway's two queries only read it and do not create a missing ledger.

Rules start empty and apply to one model scope. A proven rename of the same physical file can retain its scope; a copy or Save As does not automatically inherit rules or grants. History-continuation choices do not grant failure-policy continuity. Missing records, an empty query result or an unrecognized identity are not proof that a model is safe.

Captured context is bounded. Raw code, credentials and full argument bodies are not failure-ledger content by default. Read `complete`, omitted counts, coverage and persistence fields. If durable storage fails, the add-in retains available in-memory blocking state and attempts a local recovery record. If neither store can be written, it reports that durability is unavailable; it cannot promise recovery across restart on an unwritable volume. Keep the records when troubleshooting rather than deleting the ledger to clear a gate.

## Handle an issue in Revit

Open **History → Issues** from the Revit ribbon or History window. Select the correct model and incident, inspect the outcome, captured elements and any incomplete evidence, and enter a reason before choosing an action.

| Action | Effect |
| --- | --- |
| **Rework** | Records that the attempt will be reworked. It does not execute the revised operation. |
| **Abandon** | Closes the attempted action after review. It does not undo earlier commits, delete elements, save or discard the model. Partial or unknown outcomes require explicit confirmation that the model was checked. |
| **Accept once** | Grants one matching new attempt for supported, fully captured warnings or an exact supported dialog. It does not recommit a rolled-back transaction or retry the old call. |
| **Create rule** | Records a model-specific rule with the displayed definition, Revit year, context, count and existing-element constraints. Every observed failure must match. |
| **Revoke rule** | Disables a selected rule for future attempts. It does not undo previous changes. |
| **Allow native action once** | When available for a blocked native operation, authorizes only the displayed action, file/options and request context. It does not execute it. |

One-use grants expire after five minutes, are bound to the intended request and document revision, and can be invalidated by document changes, Undo, activation, reopening or Save As. New warning codes, unapproved existing elements, larger counts, incomplete capture, Error or Corruption cannot be accepted by a warning rule. A failed decision save preserves the previous gate/rule state.

These decisions belong to the person using Revit. There is no public MCP approval, resolve, rule-edit or `approved=true` tool. Recording a change-history reason or telling an agent that an issue is accepted does not create a Revit Issues decision. The local decision record identifies the Windows/Revit user and UI channel; it is not a digital signature or isolation from arbitrary code running in the same process.

## Query without waiting for Revit's UI

Both tools are in `meta` and remain exposed when the selected surface is read-only or send-code/history recording is disabled. They do not dispatch an ExternalEvent, retry a request or change the model, so they can answer while a native modal dialog is still open.

| Tool | Parameters |
| --- | --- |
| `revit_get_failure_records` | Supply an issued `modelScopeKey`, `incidentId` or `operationId`. Optional: `state`, `elementId`, ISO date/time `from` and `until`, `limit` (1–100, default 50), returned `cursor`, and `includeDetails` (default false). Follow `nextCursor` for further pages. |
| `revit_get_operation_status` | Required `operationId`. Returns available execution/outcome information, incident IDs and bounded final receipts. `available=false` means no durable status is available, not that the operation failed to run. |

Use identifiers from receipts; these queries do not guess a model from its title and do not accept a file path instead of an identifier. Summaries and captured details have size limits. Use details and paging without claiming omitted evidence was checked.

## Modal dialogs, busy hosts and late results

Revit failures use the failure APIs. The add-in does not press generic OK/Cancel buttons or override `Dialog_Revit_DocWarnDialog`. Without a matching rule for a supported exact dialog, Revit retains its native dialog for the user and the call can report `REVIT_OPERATION_REQUIRES_USER` with `waiting_for_user`. Other UI-dependent calls to that busy host return `REVIT_BUSY`; inspect Issues/status instead of queueing another model operation.

The current dialog adapters cover only the observed Unresolved References and Analytical Changed contexts for Revit 2022 and 2027. They require a decision or rule for the exact dialog, year, context and displayed button meaning. Other years and dialogs remain manual. Incompatible central-version dialogs and **Project Not Saved Recently** are manual-only; the add-in does not choose Save or Discard for the user. A delayed reminder associated with an earlier blocked save may be reported as correlated without claiming causation.

Caller timeout and native execution completion are different events. A timed-out or disconnected request can still finish in Revit. Retain the issued operation ID, inspect the native UI, and query the local final outcome before planning any new action. `timed_out_running`, `interrupted_unknown`, `partial`, incomplete capture or an unavailable record must never trigger an unchanged automatic retry. A completed result and actual model readback establish what happened; a responsive window alone does not.

Use a matching gateway and add-in build, and restart Revit after an add-in update. Non-read dispatch requires the controlled-failure capability handshake; `PAIRING_REQUIRED` means update the pair together. Tool discovery and an ordinary read do not prove the mutation policy ran.

## Native operation and API limits

Supported Open, Save, Save As and Synchronize pre-events check the live native context and any required one-use UI grant. A denied Sync is reported as cancelled even if the native API returns normally. If an event cannot be cancelled or its identity/options cannot be verified, the result reports enforcement unavailable or an unknown outcome; it does not claim a successful block.

A modified document's Closing event does not expose whether the caller intended save-on-close or discard. Controlled calls therefore require the user to close that document through Revit instead of guessing that choice. Application shutdown and user actions outside the MCP scope retain native handling. Save As, central upgrades, save-on-close, delayed reminders and storage-failure recovery still need their own native acceptance evidence; do not infer those guarantees from a successful build or a simple warning rollback.

`send_code` and custom baked implementations run arbitrary C# in Revit. Controlled failures are not a sandbox: another preprocessor can remove evidence before the supported adapters see it, and external side effects or arbitrary document operations may escape observation. Coverage is explicitly `dynamic_code_unverified` or `incomplete` where appropriate. Absence of captured failures never means complete design validation. See [send-code failure handling](send-code.md) and [change tracking](change-tracking.md) for the separate transaction and history boundaries.
