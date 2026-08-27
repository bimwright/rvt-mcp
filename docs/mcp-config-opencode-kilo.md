# MCP Configuration for OpenCode CLI and Kilo Code CLI

> **Audience:** anyone wiring `RvtMcp.Server.exe` (or any other stdio MCP server) into OpenCode CLI or Kilo Code CLI.
> **Last verified:** 2026-05-22 against `opencode.ai/docs` and `kilo.ai/docs`.

These two CLI agents share a **non-standard MCP config format** that differs from both Anthropic (Claude Code / Desktop) and OpenAI (Codex) conventions. Copy-pasting an `mcpServers` block from a Claude config will silently fail in either tool — they look for `mcp` (no `Servers` suffix) and require array-form `command` plus an `environment` key (not `env`).

For Anthropic clients see [`mcp-config-claude-clients.md`](./mcp-config-claude-clients.md).
For OpenAI Codex see [`mcp-config-codex.md`](./mcp-config-codex.md).

---

## 1. Quick comparison vs Claude / Codex

| Aspect | Claude (CC / Desktop) | Codex | **OpenCode** | **Kilo Code** |
|---|---|---|---|---|
| Format | JSON | TOML | **JSONC** (JSON + comments) | **JSONC** |
| Top-level key | `mcpServers` | `mcp_servers` | **`mcp`** | **`mcp`** |
| `command` field | string | string | **array** `["bin", "arg1", ...]` | **array** |
| Env-vars key | `env` | `env` | **`environment`** | **`environment`** |
| Server "type" key | implicit | implicit | **`"local"` \| `"remote"`** | **`"local"` \| `"remote"`** |
| Enable toggle | implicit | `enabled` | `enabled` | `enabled` |
| Per-server timeout | n/a | `tool_timeout_sec` (s) | `timeout` (ms) | `timeout` (ms) |
| CLI registration | `claude mcp add` | `codex mcp add` | (config file only) | `kilo mcp add` |
| Project-scope file | `.mcp.json` | `.codex/config.toml` (+trust) | `opencode.json` at repo root | `kilo.json` or `.kilo/kilo.json` |

**Three gotchas when porting a config across tools:**

1. **Top-level key has no `Servers` suffix.** It is literally `mcp`, not `mcpServers`.
2. **`command` is an array, not a string.** Write `["npx", "-y", "x"]`, not `"npx -y x"`.
3. **Env-var key is `environment`, not `env`.** Easy to miss — silent failure if you use `env`.

---

## 2. OpenCode CLI

### 2.1 Config file paths

OpenCode loads **multiple** config files and merges them in a defined precedence chain. Later sources override earlier ones on conflicting keys.

Precedence (low → high):

1. Remote config from `.well-known/opencode` HTTP endpoint
2. Global config — `~/.config/opencode/opencode.json` (macOS/Linux) or `%ProgramData%\opencode\opencode.json` (Windows)
3. Path pointed to by `OPENCODE_CONFIG` env var
4. Project config — `opencode.json` at project root
5. Files under `.opencode/` directory
6. Inline JSON from `OPENCODE_CONFIG_CONTENT` env var
7. Managed system config
8. macOS MDM managed preferences (highest)

**Windows reality check:** the official docs list `%ProgramData%\opencode`, but in practice many users (and rvt-mcp's `install.ps1`) wire to `%USERPROFILE%\.config\opencode\opencode.json` (XDG-style). OpenCode reads both — the install script uses the user-level XDG path because it does not require admin rights, while `%ProgramData%` does.

For RvtMcp, **prefer user-level XDG path on Windows**: `%USERPROFILE%\.config\opencode\opencode.json`.

### 2.2 Full JSONC format example

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "rvt-mcp": {
      "type": "local",
      "command": [
        "D:\\Projects\\bimwright\\rvt-mcp\\src\\server\\bin\\Debug\\net8.0\\RvtMcp.Server.exe"
      ],
      "environment": {
        "BIMWRIGHT_READ_ONLY": "0",
        "BIMWRIGHT_TOOLSETS": "query,create,view,sheets,families"
      },
      "enabled": true,
      "timeout": 30000
    }
  }
}
```

Remote (Streamable HTTP) variant — included for completeness, not applicable to RvtMcp:

```jsonc
{
  "mcp": {
    "some-remote": {
      "type": "remote",
      "url": "https://mcp.example.com/mcp",
      "headers": { "Authorization": "Bearer ${API_TOKEN}" },
      "oauth": {},
      "enabled": true,
      "timeout": 30000
    }
  }
}
```

### 2.3 Field reference (server entry)

| Field | Type | Server type | Notes |
|---|---|---|---|
| `type` | `"local"` \| `"remote"` | both | **Required.** Picks transport. |
| `command` | array of strings | local | **Required for local.** First element is the executable, rest are args. |
| `environment` | object | both | Env vars passed to child process. |
| `enabled` | boolean | both | Default `true`. Set `false` to keep entry but disable. |
| `timeout` | number | both | Milliseconds. Default `5000`. |
| `url` | string | remote | **Required for remote.** |
| `headers` | object | remote | Static HTTP headers (e.g., bearer token). |
| `oauth` | object \| `false` | remote | OAuth config; `false` disables. |

### 2.4 Tool-level toggles

OpenCode lets you allow / deny individual tools via glob in the top-level `tools` block, **separate** from the `mcp` block:

```jsonc
{
  "mcp": { "rvt-mcp": { /* ... */ } },
  "tools": {
    "rvt-mcp*": true,                // allow everything (default if absent)
    "rvt-mcp_revit_send_code_to_revit": false   // but deny one specific tool
  }
}
```

The tool name pattern OpenCode generates is `<server>_<tool>` (single underscore), not `mcp__<server>__<tool>` like Claude.

### 2.5 No CLI registration command

OpenCode does **not** ship a `opencode mcp add` equivalent. Configuration is file-only:

```bash
# Edit user-level config
opencode auth        # if you need to set up auth providers
# Then open opencode.json in your editor
notepad %USERPROFILE%\.config\opencode\opencode.json   # Windows
$EDITOR ~/.config/opencode/opencode.json               # macOS/Linux
```

After editing, restart `opencode` or use `/mcp` inside an interactive session to verify the server connects.

### 2.6 Per-agent enabling (advanced)

OpenCode supports defining "agents" (named tool profiles). An agent config can enable/disable individual MCP servers — useful if you want a `read-only` agent that only sees query-mode tools. See `opencode.ai/docs/agents` for the agent block schema.

---

## 3. Kilo Code CLI

Kilo Code is a fork of OpenCode with extended CLI ergonomics and a CLI-side `mcp add` command. The config format is very similar to OpenCode but **not identical** — Kilo dropped OpenCode's array-form for some legacy cases and added a richer permission model.

### 3.1 Config file paths

| Scope | Path |
|---|---|
| Global (Linux/macOS) | `~/.config/kilo/kilo.json` (also reads `kilo.jsonc`, `config.json` in the same dir) |
| Global (Windows) | `%USERPROFILE%\.config\kilo\kilo.json` |
| Project | `<project>/kilo.json` or `<project>/.kilo/kilo.json` (cleaner) |

Precedence: **project > global** (project wins on conflicting keys, no merge beyond top-level).

### 3.2 Full JSONC format example

```jsonc
{
  "mcp": {
    "rvt-mcp": {
      "type": "local",
      "command": [
        "D:\\Projects\\bimwright\\rvt-mcp\\src\\server\\bin\\Debug\\net8.0\\RvtMcp.Server.exe"
      ],
      "environment": {
        "BIMWRIGHT_READ_ONLY": "0"
      },
      "enabled": true,
      "timeout": 30000
    }
  },
  "permissions": {
    "rvt-mcp_revit_send_code_to_revit": "ask",     // prompt before running
    "rvt-mcp_*": "allow"                     // allow everything else from this server
  }
}
```

Remote (HTTP/SSE):

```jsonc
{
  "mcp": {
    "remote-example": {
      "type": "remote",
      "url": "https://mcp.example.com/mcp",
      "headers": { "Authorization": "Bearer ${KILO_TOKEN}" },
      "enabled": true,
      "timeout": 30000
    }
  }
}
```

### 3.3 Field reference

| Field | Type | Notes |
|---|---|---|
| `type` | `"local"` \| `"remote"` | Required. |
| `command` | array of strings | Required for local. |
| `environment` | object | Env vars. |
| `enabled` | boolean | Default `true`. |
| `timeout` | number (ms) | Default `5000`. |
| `url` | string | Required for remote. |
| `headers` | object | Static headers for remote. |

### 3.4 Tool permissions

Kilo unifies permissions for **built-in tools and MCP tools** under one top-level `permissions` key:

```jsonc
{
  "permissions": {
    "<server>_<tool>": "allow" | "ask" | "deny"
  }
}
```

Naming pattern: `<server-name>_<tool-name>` with single underscore. Glob `*` supported.

Example for RvtMcp's locked-down read-only mode:
```jsonc
{
  "permissions": {
    "rvt-mcp_revit_send_code_to_revit": "deny",
    "rvt-mcp_revit_batch_execute": "ask",
    "rvt-mcp_*": "allow"
  }
}
```

### 3.5 CLI commands

Unlike OpenCode, Kilo ships a complete `mcp` subcommand:

```bash
kilo mcp list                      # show all configured servers + status
kilo mcp add                       # interactive add wizard
kilo mcp auth <name>               # OAuth flow for an MCP server
kilo mcp logout <name>             # clear OAuth creds
kilo mcp debug                     # diagnostic dump
```

Inside an interactive session, the slash command `/mcps` toggles servers on/off without editing config.

### 3.6 Context budget warning

Kilo's official docs include this caution prominently:

> "MCP servers add to your context, so be careful with which ones you enable. Certain MCP servers with many tools can quickly add up and exceed the context limit."

For RvtMcp (40 tools by default; 229 with `--toolsets all`, or 232 with adaptive bake), this is **the** issue. Two mitigations:

1. Use `enabled_tools` filter via permissions (`"rvt-mcp_*": "deny"` then enable specific ones with `"rvt-mcp_revit_create_grid": "allow"`).
2. Run RvtMcp with a narrower toolset via env var: `"environment": { "BIMWRIGHT_TOOLSETS": "query,view" }`.

---

## 4. Common pitfalls when porting a Claude/Codex config

If you have a working Claude `mcpServers` block and try to copy it to OpenCode/Kilo, **three things will break silently**:

| Wrong (Claude/Codex style) | Right (OpenCode/Kilo style) |
|---|---|
| `"mcpServers"` | `"mcp"` |
| `"command": "C:\\path\\to.exe"` | `"command": ["C:\\path\\to.exe"]` |
| `"env": { "X": "1" }` | `"environment": { "X": "1" }` |

Symptom: the server entry is parsed but the launch fails or no tools appear. There's no schema validation error — both tools just don't show the server in `/mcp`.

A fourth subtle one for **Kilo specifically**: tool permissions use `<server>_<tool>` (single underscore), not `mcp__<server>__<tool>` (Claude's double-underscore prefix). Permission rules written in Claude's pattern won't match anything in Kilo.

---

## 5. How RvtMcp's `install.ps1` handles these (v0.5+)

`scripts/install.ps1` wires both OpenCode and Kilo automatically via `Add-OpencodeEntry` and `Add-KiloEntry` (added in v0.5). Both functions emit the same JSONC shape:

```powershell
$entry = [ordered]@{
    type    = 'local'
    command = @($t.ServerCmd) + @($t.Args)   # array form ✅
    enabled = $true
}
if ($t.PSObject.Properties.Name -contains 'Env' -and $t.Env -and $t.Env.Count -gt 0) {
    $entry['environment'] = $t.Env           # optional environment block ✅
}
$cfg['mcp'][$k] = $entry
```

Differences:

| Aspect | `Add-OpencodeEntry` | `Add-KiloEntry` |
|---|---|---|
| Target file | `%USERPROFILE%\.config\opencode\opencode.json` | `%USERPROFILE%\.config\kilo\kilo.json` |
| Extra fields | none | `timeout = 30000` (Kilo's default 5000 ms is too short for Revit cold-start) |
| Auto-create file if missing | no (skip if absent) | yes when `-Client kilo` explicit; no when `-Client Auto` |

Both honor an optional `Env` hashtable on each target so RvtMcp env knobs (`BIMWRIGHT_TOOLSETS`, `BIMWRIGHT_READ_ONLY`) can be wired per-server. Currently `Get-RvtMcpClientTargets` does not emit `Env`; pass `-Client kilo` and edit the resulting JSON manually if you need env knobs (or extend the script to populate `Env` from CLI flags).

Run `pwsh .\install.ps1 -Client kilo -WhatIf` to preview, `-Client kilo` to apply.

---

## 6. Sources

### OpenCode

- [MCP servers — OpenCode docs](https://opencode.ai/docs/mcp-servers/)
- [Config reference — OpenCode docs](https://opencode.ai/docs/config/)
- [CLI reference — OpenCode docs](https://opencode.ai/docs/cli/)
- [How to Add MCP to OpenCode (Composio guide)](https://composio.dev/content/mcp-with-opencode)

### Kilo Code

- [MCP overview — Kilo docs](https://kilo.ai/docs/automate/mcp/overview)
- [Using MCP in CLI — Kilo docs](https://kilo.ai/docs/automate/mcp/using-in-cli)
- [Kilo Code CLI command reference](https://kilo.ai/docs/code-with-ai/platforms/cli-reference)
- [Kilo settings reference](https://kilo.ai/docs/getting-started/settings)
- [Kilo-Org/kilocode on GitHub](https://github.com/Kilo-Org/kilocode)
