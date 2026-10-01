# Element changes during MCP commands

Development source includes `_changes` metadata when Revit reports committed element
changes during a synchronous MCP command. It needs a server and plugin built with
this feature. Existing 1.0.0 candidate bundles do not acquire it automatically.

The metadata covers typed tools, `send_code_to_revit`, `batch_execute` and baked tools
through the common command dispatcher. It is not a new tool and does not grant write
permission. Events outside the command scope, including subsequent manual Undo, are
not captured. Read commands with no document changes keep their existing response.

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

Model file/central/cloud paths and persistent model identity are not collected in this
increment. The server uses an allowlist for agent-facing metadata. With call logging
enabled, the journal adds a separate `Changes` field, not subject to the 2,048-character
`Result` text truncation. Logging remains off by default. This is not a persistent
reason database, a guarantee about user edits outside MCP, or an engineering check.

The `revit_change` prompt uses available metadata to compare the agreed scope, while
still requiring readback of actual values. No metadata from an older plugin is not
evidence that nothing changed.
