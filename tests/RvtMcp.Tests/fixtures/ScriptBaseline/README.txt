P00 synthetic and sanitized baseline fixtures

outcome-pairs.json contains 17 independently mapped audit cases, each with a
legacy return and a task_v1 rewrite. Customer error text is replaced by synthetic
messages; original return-field names, booleans, rollback strings and native
receipt semantics are retained. Per-case private provenance stays outside Git.
The task_v1 expectations describe future acceptance; P00 does not implement or
claim that contract. In particular, script-reported rollback does not turn an
unknown native outcome into rolled_back.

legacy-bake-v1.sql freezes the populated schema at version 1. Tests construct it
directly before invoking the production migrator. The legacy DLL is emitted from
legacy-command.txt against synthetic API/interface stand-ins: it is a genuine
managed assembly for byte-preservation tests, not a Revit-native verified tool.
No test dispatches a command to Revit or opens the user's bake.db.

history/*.jsonl are sanitized representatives of archived/current legacy logs.
Their original statuses and absent new fields are preserved. Exact source lines,
archive snapshots and transformation provenance are private. history/mcp-calls.version
is the existing format marker; additive field work must not bump it through the
destructive old-log purge path. Tests read/copy these files into temporary folders.

These fixtures intentionally contain no private paths, model geometry, credentials,
customer scripts or real model/process identities.
