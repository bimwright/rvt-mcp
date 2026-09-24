# MCP Configuration for OpenAI Codex (CLI, Desktop, IDE Extension)

> **Audience:** anyone wiring `rvt-mcp.exe` (or any other stdio MCP server) into one of OpenAI's Codex clients.
> **Last verified:** 2026-09-25 against `developers.openai.com/codex/mcp`, `developers.openai.com/codex/config-reference`, and the open `openai/codex` issue tracker.
> **Canonical procedure:** [mcp-client-wiring.md](mcp-client-wiring.md) — this file is the deeper per-vendor reference.

Codex is OpenAI's coding agent. It ships as three surfaces that talk to the same model but have **different config-loading rules** — this matters because a setup that works in Codex CLI may silently fail in Codex Desktop.

For Anthropic clients (Claude Code CLI, Claude Code VS Code extension, Claude Desktop), see [`mcp-config-claude-clients.md`](./mcp-config-claude-clients.md).

---

## 1. Three Codex surfaces, one config file (mostly)

| Client | Reads | Writes (via UI) | Status |
|---|---|---|---|
| **Codex CLI** (`codex` in terminal) | `~/.codex/config.toml` + `<project>/.codex/config.toml` (if trusted) | Same | ✅ Full support |
| **Codex IDE extension** (VS Code, JetBrains) | **Same files as CLI** — config is shared | Same | ✅ Full support, official confirmed |
| **Codex Desktop** (standalone app) | **Only `~/.codex/config.toml`** | `~/.codex/config.toml` | ⚠ Bug — ignores project-scoped config |

The Codex docs state explicitly: *"The CLI and the IDE extension share this configuration."* Desktop is the odd one out: as of 2026-05-22, [openai/codex#13025](https://github.com/openai/codex/issues/13025) reports — and OpenAI has not closed — that Codex Desktop silently ignores any `.codex/config.toml` placed inside a project root. Only `~/.codex/config.toml` (user-scope) is read.

**Implication for RvtMcp:** if your users include Codex Desktop, you **must** wire RvtMcp into user-scope config. Project-scope wiring is invisible to Desktop.

### Config file paths

| OS | Path |
|---|---|
| Windows | `%USERPROFILE%\.codex\config.toml` |
| macOS | `~/.codex/config.toml` |
| Linux | `~/.codex/config.toml` |

`~` expands to the home directory on every OS. Codex creates the file the first time you run `codex` if it's missing.

---

## 2. TOML format

Codex uses **TOML**, not JSON. The MCP server registry lives under tables named `[mcp_servers.<server-name>]`.

### Minimal stdio entry for RvtMcp

```toml
[mcp_servers.rvt-mcp]
command = "C:\\Users\\<user>\\AppData\\Local\\RvtMcp\\rvt\\server\\current\\rvt-mcp.exe"
args = []
```

### Full entry with all knobs

```toml
[mcp_servers.rvt-mcp]
command = "C:\\Users\\<user>\\AppData\\Local\\RvtMcp\\rvt\\server\\current\\rvt-mcp.exe"
args = ["--target", "2024"]         # optional: pin a Revit calendar year (4 digits, never R-codes)
cwd = "%LOCALAPPDATA%\\RvtMcp"      # optional: working dir for the process

# Lifecycle
enabled = true                       # default true; set false to keep entry but disable
required = false                     # default false; if true, Codex fails startup when server is unreachable
startup_timeout_sec = 10             # default 10 — bump if the server is slow to bind to stdio
tool_timeout_sec = 60                # default 60 — per-tool call timeout in seconds

# Tool filtering
enabled_tools = []                   # allow list — empty means "all tools allowed"
disabled_tools = ["revit_send_code_to_revit"]   # deny list — applied after allow list
default_tools_approval_mode = "auto" # "auto" | "prompt" | "approve" (auto = no prompt, approve = always ask)

# Per-tool approval override
[mcp_servers.rvt-mcp.tools.revit_batch_execute]
approval_mode = "prompt"             # ask before each revit_batch_execute call even if server default is "auto"

# Environment variables passed to the server process
[mcp_servers.rvt-mcp.env]
BIMWRIGHT_READ_ONLY = "0"
BIMWRIGHT_TOOLSETS = "query,create,view,sheets,families"
```

### Streamable HTTP entry (not applicable to RvtMcp, shown for completeness)

```toml
[mcp_servers.some-http-server]
url = "https://mcp.example.com/mcp"
bearer_token_env_var = "EXAMPLE_TOKEN"   # Codex reads token from this env var

[mcp_servers.some-http-server.http_headers]
X-Custom-Header = "value"

[mcp_servers.some-http-server.env_http_headers]
X-Trace-Id = "TRACE_ID_ENV_VAR"          # header value pulled from env at call time
```

---

## 3. Full reference of `mcp_servers.<id>.*` keys

Source: `developers.openai.com/codex/config-reference`.

| Key | Type | Default | Notes |
|---|---|---|---|
| `command` | string | — | **Required** for stdio. Launcher binary path. |
| `args` | array<string> | `[]` | CLI args passed to `command`. |
| `cwd` | string | inherits Codex's cwd | Working dir for the child process. |
| `url` | string | — | **Required** for HTTP (mutually exclusive with `command`). |
| `enabled` | bool | `true` | Toggle without deleting the entry. |
| `required` | bool | `false` | If true, Codex refuses to start when the server is unreachable. |
| `startup_timeout_sec` | number | `10` | Seconds to wait for handshake. |
| `tool_timeout_sec` | number | `60` | Seconds per tool call. |
| `enabled_tools` | array<string> | `[]` (means all) | Allow list of tool names. |
| `disabled_tools` | array<string> | `[]` | Deny list (applied after allow list). |
| `default_tools_approval_mode` | `"auto"` \| `"prompt"` \| `"approve"` | inherits global | Approval policy unless a per-tool override exists. |
| `tools.<tool>.approval_mode` | same enum | — | Per-tool override. |
| `env` | map<string,string> | `{}` | Environment variables forwarded to the server. |
| `env_vars` | array<string \| object> | `[]` | Additional env-var names to whitelist (passed through from Codex's env). |
| `http_headers` | map<string,string> | `{}` | Static HTTP headers (HTTP servers only). |
| `env_http_headers` | map<string,string> | `{}` | HTTP headers sourced from env vars. |
| `bearer_token_env_var` | string | — | Env-var name holding a bearer token (HTTP servers). |
| `scopes` | array<string> | — | OAuth scopes to request. |
| `oauth_resource` | string | — | RFC 8707 resource parameter (OAuth). |

---

## 4. CLI registration command

You can edit `config.toml` by hand OR use the CLI:

```bash
# Basic stdio
codex mcp add rvt-mcp -- "%LOCALAPPDATA%\\RvtMcp\\rvt\\server\\current\\rvt-mcp.exe"

# With env vars
codex mcp add rvt-mcp \
  --env BIMWRIGHT_READ_ONLY=0 \
  --env BIMWRIGHT_TOOLSETS=query,create,view \
  -- "%LOCALAPPDATA%\\RvtMcp\\rvt\\server\\current\\rvt-mcp.exe"

# Pin a specific Revit year
codex mcp add rvt-mcp-2024 -- "%LOCALAPPDATA%\\RvtMcp\\rvt\\server\\current\\rvt-mcp.exe" --target 2024
```

The `--` separates Codex's own flags from the command + args passed to the MCP server, identical convention to `claude mcp add`.

Other commands:

```bash
codex mcp list             # show all configured servers
codex mcp remove rvt-mcp   # delete entry
# Inside an active session:
/mcp                       # show connection status + tools
```

The CLI rewrites `~/.codex/config.toml` — which means Codex Desktop will see the new entry on its next launch.

---

## 5. Project-scoped config (CLI + IDE only — NOT Desktop)

Codex supports per-project config overrides at `<project-root>/.codex/config.toml`. Two prerequisites:

### 5.1 The project must be marked trusted

In `~/.codex/config.toml`:

```toml
[projects."D:/Projects/bimwright/rvt-mcp"]
trust_level = "trusted"
```

Codex sees this and only then loads `<project-root>/.codex/config.toml`. Untrusted (or unmarked) projects have their `.codex/` layer skipped entirely — this is the security guard against malicious repo configs.

Path must match exactly how Codex sees the project root (typically the absolute path with forward slashes on every OS, but quote it to be safe).

### 5.2 What project-scoped CAN override

Project config can override most keys but **cannot** override these (machine-local-only):

- Provider config (`openai_base_url`, `chatgpt_base_url`, etc.)
- Auth tokens
- Notification routing
- Profile selection
- Telemetry routing

These five categories are reserved for `~/.codex/config.toml` regardless of what the project file says.

### 5.3 Why this fails in Codex Desktop

[openai/codex#13025](https://github.com/openai/codex/issues/13025): Codex Desktop ignores project-scoped config files even when the project is trusted. The MCP server registered in `<project-root>/.codex/config.toml` simply doesn't appear in the Desktop app's `/mcp` panel.

Status as of 2026-05-22: **issue still open**, no patch released. If your install script supports Codex Desktop users, **register the server in `~/.codex/config.toml`** so Desktop picks it up.

---

## 6. Wiring Codex for RvtMcp

The installer **no longer edits client configs** — connect Codex with the procedure in [mcp-client-wiring.md](mcp-client-wiring.md):

```powershell
codex mcp add rvt-mcp -- "$env:LOCALAPPDATA\RvtMcp\rvt\server\current\rvt-mcp.exe"
codex mcp get rvt-mcp --json
```

It writes to **user-scope** `~/.codex/config.toml`, which is the only file Codex Desktop reads. CLI and IDE extension also read this file, so a single registration covers all three Codex surfaces. ✅

Watch for legacy entries from the previous `bimwright-rvt-r22..r27` naming — report them and remove only with the user's consent. Orphan `[mcp_servers.<name>.env]` tables left behind by hand edits break the whole config with `invalid transport`.

After wiring:

```bash
# Verify from any Codex client
codex mcp list                    # CLI
# or open Codex Desktop and check the MCP panel
```

---

## 7. Tool exposure to the agent

Codex's docs do not currently publish the exact prefix format Codex uses internally for MCP tool names (Anthropic publishes `mcp__<server>__<tool>` with a 64-char limit; OpenAI has not documented an equivalent). Empirically Codex prefixes tools with the server name but accepts longer names than Claude Code's 64-char ceiling. Until OpenAI publishes a spec, **keep tool names ≤ 51 chars** to stay safe across both vendors.

For RvtMcp v0.5+ (longest tool name = `revit_measure_distance_between_elements` at 39 chars), this is well within both limits.

---

## 8. Comparison cheat-sheet vs Anthropic clients

| Aspect | Claude Code (CLI + VS Code ext.) | Claude Desktop | Codex CLI + IDE ext. | Codex Desktop |
|---|---|---|---|---|
| Config format | JSON | JSON | **TOML** | **TOML** |
| Top-level key | `mcpServers` | `mcpServers` | `mcp_servers` | `mcp_servers` |
| User-scope path (Windows) | `%USERPROFILE%\.claude.json` | `%APPDATA%\Claude\claude_desktop_config.json` | `%USERPROFILE%\.codex\config.toml` | `%USERPROFILE%\.codex\config.toml` |
| Project-scope file | `.mcp.json` at repo root | ❌ Not supported | `.codex/config.toml` + `trust_level="trusted"` | ❌ Bug #13025 — silently ignored |
| Add via CLI | `claude mcp add` | n/a (edit JSON) | `codex mcp add` | n/a (edit TOML) |
| Per-server timeout | Global `MCP_TIMEOUT` env | Same | `startup_timeout_sec`, `tool_timeout_sec` | Same |
| Tool filter | Permissions/allowlist patterns | Same | `enabled_tools`, `disabled_tools` | Same |
| Per-tool approval | Permission rules in settings | Same | `default_tools_approval_mode` + per-tool overrides | Same |
| Hot reload after edit | Yes (`/mcp` reconnect) | No — restart app | Yes (`/mcp` reconnect) | No — restart app |
| Tool name prefix shown to model | `mcp__<server>__<tool>` (64-char hard limit) | Same | Not officially documented | Not officially documented |

---

## 9. Sources

- [Model Context Protocol – Codex (official docs)](https://developers.openai.com/codex/mcp)
- [Configuration Reference – Codex (official docs)](https://developers.openai.com/codex/config-reference)
- [Advanced Configuration – Codex](https://developers.openai.com/codex/config-advanced)
- [Codex CLI command-line reference](https://developers.openai.com/codex/cli/reference)
- [openai/codex#13025 — Desktop ignores project .codex/config.toml](https://github.com/openai/codex/issues/13025)
- [openai/codex#3441 — Codex does not use MCP servers in config.toml (CLI-side historical issue)](https://github.com/openai/codex/issues/3441)
