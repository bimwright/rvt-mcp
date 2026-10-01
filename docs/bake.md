# Adaptive Bake

Adaptive bake turns repeated local Revit workflows into personal tools only after explicit opt-in and acceptance. It is off by default.

## Enable

Set the master flag before starting the MCP server:

```powershell
$env:BIMWRIGHT_ENABLE_ADAPTIVE_BAKE = "1"
rvt-mcp
```

Or set JSON config:

```json
{
  "enableAdaptiveBake": true
}
```

Config path: `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json`.

`BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` takes effect at the next MCP server start. If you change the flag while a Claude Code session is active, restart the MCP connection with disconnect -> reconnect via `/mcp` so `list_bake_suggestions`, `accept_bake_suggestion`, and `dismiss_bake_suggestion` appear.

`send_code_to_revit` is part of the default ToolBaker surface and does not require adaptive bake. Adaptive bake only adds `list_bake_suggestions`, `accept_bake_suggestion`, `dismiss_bake_suggestion`, and local usage analysis for suggestions.

## Optional Code Cache

By default, adaptive bake does not keep raw `send_code_to_revit` code bodies for clustering.

Set this only if you want send-code clusters to be eligible for suggestion generation:

```powershell
$env:BIMWRIGHT_CACHE_SEND_CODE_BODIES = "1"
```

Or set JSON config:

```json
{
  "cacheSendCodeBodies": true
}
```

Even with code caching enabled, long-lived journals and usage logs store redacted or hashed content. Code samples used for cluster-A condensation are redacted before any naming or condensation step.

## Optional send_code body journal (TTL)

For local improvement and troubleshooting, you can opt-in to logging raw `send_code_to_revit` bodies into a TTL-bounded journal. This is independent of `cacheSendCodeBodies`.

Enable this by setting JSON config fields:
```json
{
  "persistSendCodeBodies": true,
  "persistSendCodeBodiesUntil": "2026-07-09T18:00:00Z"
}
```
Or via CLI:
```powershell
rvt-mcp --persist-send-code-bodies
rvt-mcp --persist-send-code-bodies-for 4h
```

**Key Policies:**
- **Default Privacy:** No journal writes occur unless `persistSendCodeBodies` is `true` AND `persistSendCodeBodiesUntil` is a valid UTC date in the future.
- **TTL bounds:** Min 1 hour, default 4 hours, max 2 days. The CLI/Env clamps values automatically.
- **Journal file:** written to `%LOCALAPPDATA%\Bimwright\rvt-mcp\send-code-journal.jsonl`.
- **Rotation:** rotates at ~5MB.
- **Retention:** deleted automatically 7 days after the persist window expires or is disabled.
- **Redaction:** `BakeRedactor.RedactForBake` (redacts local paths, UNC paths, and sensitive tokens) is applied to the persisted code.

## Local Storage

All adaptive-bake storage is local to the current Windows user under `%LOCALAPPDATA%\Bimwright\rvt-mcp\`.

Key files:

- `%LOCALAPPDATA%\Bimwright\rvt-mcp\usage.jsonl` - append-only local usage events.
- `%LOCALAPPDATA%\Bimwright\rvt-mcp\bake.db` - SQLite suggestions and accepted-tool registry.
- `%LOCALAPPDATA%\Bimwright\rvt-mcp\bake-audit.jsonl` - local suggestion and lifecycle audit events.

The server is the only writer for `bake.db`. The Revit plugin opens `bake.db` read-only, loads accepted tools into memory, and owns the runtime/ribbon surface.

## What Gets Logged

When adaptive bake is enabled, Bimwright can log local usage signals such as:

- tool name and source type (`send_code`, preset, macro).
- normalized argument shape and hashes.
- success/failure result.
- `send_code_to_revit` body hash and code length.
- redacted cluster material when `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1`.

It should not log raw file paths, URLs, tokens, project names, or raw send-code bodies into long-lived usage and journal records. Sensitive literals are redacted or replaced with hashes before persistence.

## Suggestions

Repeated patterns become suggestions. The suggestion lifecycle is user-controlled:

- **Accept** - compile/apply the suggestion and add it to the accepted-tool registry after the plugin succeeds.
- **Snooze 30d** - hide the suggestion temporarily.
- **Never** - keep the suggestion dismissed. For send-code patterns, Bimwright can create a local GitHub issue draft URL for a roadmap gap signal; it does not submit anything automatically.

Agent surface:

```text
list_bake_suggestions
accept_bake_suggestion
dismiss_bake_suggestion
```

Use `accept_bake_suggestion` for new bakes. The legacy `bake_tool` tool was removed in v0.3.0 and is not available.

## Accepted Tools

Accepted tools are discovered through:

```text
list_baked_tools
```

Run one by name:

```text
run_baked_tool name=<tool_name>
```

In v0.3.x, baked tools never appear directly in the native MCP tool list. The accepted-tool index is the stable agent entry point.

Accepted tools can also appear as Revit ribbon buttons when the accepted suggestion was created with ribbon output. Ribbon buttons and MCP execution use the same accepted bake record.

## Archive Behavior

Accepted tools have lifecycle metadata. When a tool is archived, Bimwright keeps registry history but removes it from the active ribbon/runtime path. `run_baked_tool` returns archived-state guidance instead of executing it, and `list_baked_tools` remains the place to inspect active tools and replacements.

Archive is for tools that should not stay on the working ribbon but should remain auditable. Prefer archiving over deleting local history.

## Cross-Revit Compatibility

Each accepted tool records a compatibility map by Revit version. The origin version is the version that accepted or first compiled the bake.

When you run a baked tool from another Revit version, `run_baked_tool` may warn that the tool is untested for that version, then attempt execution and record the result. If lazy compile or execution fails, the failure is stored per version so future runs can warn with the last error.

Treat cross-version success as evidence, not a guarantee. Revit API drift can still break a baked tool between years.
