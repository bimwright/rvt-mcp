# Wiring rvt-mcp into MCP clients

Procedure for AI agents connecting `rvt-mcp` to a user's MCP client. The
installer deliberately never edits client configs — this document is the
agent-side half. Last verified on a live machine: 2026-09-25.

## The contract (same for every client)

| Field | Value |
|---|---|
| Entry name | `rvt-mcp` — exactly one entry per client |
| Transport | stdio |
| Command | `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe` (absolute) |
| Args | none required (`--toolsets all`, `--read-only` optional) |

The `current` path never changes across upgrades — wire once, upgrade freely.

## Universal procedure

1. **Detect** the client (`where.exe <cli>` or its config path exists).
2. **Locate** the config or native CLI (per-client table below).
3. **Inspect** existing entries before writing: an `rvt-mcp` entry, legacy
   `bimwright-rvt*` entries, entries with custom launchers/wrappers.
4. **Back up** the config file (`Copy-Item $f "$f.bak"`) before editing.
5. **Show** the exact command or diff; get the user's approval.
6. **Apply** the edit — minimal text change, never a full JSON round-trip
   (that loses comments and reorders keys).
7. **Verify** with the client's own `list`/`get` command when one exists, else
   re-parse the file.
8. **Handshake**: confirm `rvt-mcp` appears in the client's `tools/list`.
9. **Restart** the client so it spawns the server. A Revit-dependent call
   (`revit_get_current_view_info`) additionally needs Revit open with a model.

Edge-case rules, all clients:

- `rvt-mcp` pointing at `...\server\<version>\rvt-mcp.exe` (pre-0.6.3 layout)
  → repoint `command` to the `current` path, keep its args/env.
- `bimwright-rvt*` entries → report them; remove only with consent.
- Custom launcher/wrapper command → report; never silently replace.
- Never delete unrelated entries. Re-running this procedure must not
  duplicate the entry.

## Per-client procedures

### Claude Code (CLI — preferred path)

```powershell
claude mcp add -s user rvt-mcp -- "$env:LOCALAPPDATA\RvtMcp\rvt\server\current\rvt-mcp.exe"
claude mcp get rvt-mcp     # expect Scope: User config, Status: Connected
```

- `-s user` is required — default scope is *local* (one project) and a stale
  local entry shadows the user one. Remove it from the project directory:
  `claude mcp remove rvt-mcp -s local`.
- Config lives in `~/.claude.json`; `CLAUDE_CONFIG_DIR` redirects it.
- Running `claude` processes can rewrite `.claude.json` from memory — check
  the file again after they exit if entries reappear.

### Claude Desktop (file)

- MSIX (this machine): `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json` — **check this first**.
- Classic fallback: `%APPDATA%\Claude\claude_desktop_config.json`.
- Key `mcpServers`, standard entry shape. Fully quit the app (tray included)
  before editing, then relaunch.

### Codex (CLI)

```powershell
codex mcp add rvt-mcp -- "$env:LOCALAPPDATA\RvtMcp\rvt\server\current\rvt-mcp.exe"
codex mcp get rvt-mcp --json   # verify transport.command
```

- `mcp add` **replaces the whole entry** — when repointing, carry existing
  args/env forward. Orphan `[mcp_servers.<name>.env]` tables break the config
  (`invalid transport`).
- User scope = `~/.codex/config.toml` (`CODEX_HOME` redirects). Codex CLI,
  Desktop and IDE share it.

### OpenCode (file)

- `~/.config/opencode/opencode.json` (JSONC). Top key is `mcp`, `command` is
  an **array**, env key is `environment`:

```jsonc
"mcp": { "rvt-mcp": { "type": "local", "command": ["C:\\...\\rvt-mcp.exe"], "enabled": true } }
```

### Kilo (file)

- `~/.config/kilo/kilo.jsonc` — OpenCode shape (`mcp`, array `command`,
  `environment`). `kilo mcp add` accepts no local-command flag, so file edit
  is the documented path.
- Tool-permission patterns use single underscores (`rvt-mcp_revit_*`);
  last matching rule wins — append new rules, don't prepend.

### Grok (CLI)

```powershell
grok mcp add rvt-mcp "$env:LOCALAPPDATA\RvtMcp\rvt\server\current\rvt-mcp.exe" --transport stdio
grok mcp list; grok mcp doctor
```

- Writes user scope (`~/.grok/config.toml`). A **project-scope**
  `./.grok/config.toml` entry with the same name shadows it when run from
  that directory — check `grok mcp list` for `(project)` markers;
  `grok mcp remove` without `-s` removes from **all** scopes.

### Cursor (file)

- Global `~/.cursor/mcp.json` (preferred), project `.cursor/mcp.json`.
  Key `mcpServers`, standard shape.

### VS Code (file)

- `%APPDATA%\Code\User\mcp.json` (user) or `.vscode/mcp.json` (workspace).
  Top key is **`servers`** and the entry needs `"type": "stdio"`:

```json
"servers": { "rvt-mcp": { "type": "stdio", "command": "C:\\...\\rvt-mcp.exe", "args": [] } }
```

### Cline / Roo-style VS Code extensions (file)

- `%APPDATA%\Code\User\globalStorage\<publisher>.<ext>\settings\cline_mcp_settings.json`
  (e.g. `saoudrizwan.claude-dev`). Key `mcpServers`, standard shape.

### Zed (file — JSONC)

- `%APPDATA%\Zed\settings.json` (comments allowed — edit minimally).
  Top key is **`context_servers`**, command is an object:

```jsonc
"context_servers": { "rvt-mcp": { "command": { "path": "C:\\...\\rvt-mcp.exe", "args": [] } } }
```

### Gemini CLI (file)

- `~/.gemini/settings.json`. Key `mcpServers`, standard shape.

### Antigravity (file)

- `~/.gemini/antigravity/mcp_config.json` (on some installs it is a symlink to
  `~/.gemini/config/mcp_config.json`). Key `mcpServers`, standard shape.

### Devin (file)

- `%APPDATA%\Devin\mcp_config.json` (user) — key `mcpServers`, standard shape.
- Repo `.mcp.json` is also read — check for a project-scope duplicate.

### LM Studio (file)

- `~/.lmstudio/mcp.json`. Key `mcpServers`, standard shape.

### kun (file)

- `~/.kun/mcp.json`. Top key is **`servers`** (not `mcpServers`); match the
  existing entries' shape: `{ "command": "...", "args": [], "env": {}, "url": null }`.

### Kiro (file)

- `~/.kiro/settings/mcp.json` (user) or `.kiro/settings/mcp.json` (workspace).
  Create the file/dir if absent. Key `mcpServers`, standard shape.

### Qwen Code (file)

- `~/.qwen/settings.json` (create if absent). Key `mcpServers`, standard
  shape (Gemini CLI fork).

### Windsurf (file)

- `~/.codeium/windsurf/mcp_config.json` (may be empty — write a full
  `{ "mcpServers": { ... } }` object). Standard shape.

### Cherry Studio (no safe file path — UI only)

MCP config lives in leveldb; do not edit files. Either:

- **Deeplink** (verified schema): build
  `cherrystudio://mcp/install?servers=<base64>` where the base64 decodes to
  `{"mcpServers":{"rvt-mcp":{"command":"C:\\...\\rvt-mcp.exe","args":[]}}}`
  and hand the URL to the user to open.
- **UI import**: Settings → MCP Servers → Add → import the same JSON.

## Verify

1. Client-native check where available (`claude mcp get`, `codex mcp get
   --json`, `grok mcp doctor`, `kilo mcp list`).
2. `tools/list` shows `rvt-mcp` tools — default surface is 40 tools
   (`query,create,view,meta`), expecting `revit_get_current_view_info`.
3. With Revit 2022–2027 open and a model loaded, call
   `revit_get_current_view_info` → `{ "view_name": ..., "view_type": ...,
   "project_name": ... }`.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Entry exists but tools missing | Client not restarted, or a scope shadow (local/project entry wins) | Restart client; check the other scope (`claude mcp list`, `grok mcp list`, repo `.mcp.json`) |
| Config parse error after edit | Whole-file rewrite lost JSONC comments or left a dangling comma | Restore `.bak`; re-apply as minimal text edit |
| `invalid transport` (Codex) | Orphan `[mcp_servers.<name>.env]` table | Remove the orphan table |
| Client can't spawn server | Command path wrong or exe missing | Re-check the `current` path; run `rvt-mcp.exe --help` |
| Two `rvt-mcp` entries | Procedure ran twice with different scope/shape | Remove the extra via the client's remove command or a minimal edit |
