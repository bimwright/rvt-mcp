# MCP Configuration for Claude Clients (Claude Code CLI, VS Code Extension, Claude Desktop)

> **Audience:** anyone wiring `RvtMcp.Server.exe` (or any other stdio MCP server) into a Claude client.
> **Last verified:** 2026-05-22 against official docs at `code.claude.com/docs/en/mcp` and `support.claude.com`.

This document consolidates the research needed to register and operate the RvtMcp server with the three first-party Claude clients, plus the protocol-level constraints (name length, tool prefix format, Tool Search behavior) that determine whether agents can actually discover your tools.

---

## 1. Three clients, three config files

All three clients speak the same Model Context Protocol over stdio, but store their server registry in different files. **A single `RvtMcp.Server.exe` binary works for all three** — only the registration entry differs.

| Client | Config file (Windows) | Native UI to edit |
|---|---|---|
| **Claude Code CLI** | `%USERPROFILE%\.claude.json` (user scope) **or** `.mcp.json` in repo root (project scope) **or** `~/.claude.json` per-project block (local scope) | `claude mcp add` / `claude mcp list` / `/mcp` panel |
| **Claude Code VS Code extension** (v2.1.69+) | Same as Claude Code CLI — extension is a wrapper around the CLI | `/mcp` dialog in chat panel (no JSON editing needed) |
| **Claude Desktop** | `%APPDATA%\Claude\claude_desktop_config.json` | Settings → Developer → "Edit Config" |

On macOS Claude Desktop lives at `~/Library/Application Support/Claude/claude_desktop_config.json`. Other clients are CLI-driven and identical across platforms.

---

## 2. Claude Code CLI

### 2.1 Registration scopes

Source: `code.claude.com/docs/en/mcp` §"MCP installation scopes".

| Scope | Visible in | Stored at | Shared via VCS |
|---|---|---|---|
| `local` (default) | Current project only, just you | `~/.claude.json` (per-project block) | No |
| `project` | Current project, whole team | `.mcp.json` at project root | **Yes** |
| `user` | All projects, just you | `~/.claude.json` (top-level block) | No |

Precedence when same server name appears in multiple scopes: **local > project > user**.

### 2.2 Registration commands

Source: `code.claude.com/docs/en/mcp` §"Add an MCP server".

**General form:**
```bash
claude mcp add [options] <name> -- <command> [args...]
```

> ⚠ All options (`--transport`, `--env`, `--scope`, `--header`) **must come before** `<name>`. The `--` separates `<name>` from the command + args passed to the server.

**Examples for RvtMcp:**

```bash
# Local scope (default): only this project
claude mcp add rvt-mcp -- "D:/Projects/bimwright/rvt-mcp/src/server/bin/Debug/net8.0/RvtMcp.Server.exe"

# User scope: every project on this machine
claude mcp add rvt-mcp --scope user -- "D:/Projects/bimwright/rvt-mcp/src/server/bin/Debug/net8.0/RvtMcp.Server.exe"

# Project scope: write into .mcp.json (commit to repo, team uses it)
claude mcp add rvt-mcp --scope project -- "%LOCALAPPDATA%\\RvtMcp\\server\\0.4.0\\RvtMcp.Server.exe"

# Pin a specific Revit year (server otherwise auto-detects)
claude mcp add rvt-mcp-2024 --scope user -- "...\\RvtMcp.Server.exe" --target 2024
```

**Useful operations:**
```bash
claude mcp list                  # show all configured servers + connection status
claude mcp get rvt-mcp           # show details + OAuth status for one server
claude mcp remove rvt-mcp        # remove
claude mcp reset-project-choices # reset .mcp.json approval prompts
```

### 2.3 JSON-import shortcut

Source: `code.claude.com/docs/en/mcp` §"Importing from Claude Desktop".

```bash
# Add a server by raw JSON snippet (handy when you have a config from someone else)
claude mcp add-json rvt-mcp '{"type":"stdio","command":"D:/.../RvtMcp.Server.exe","args":[]}'

# Import all servers from Claude Desktop's claude_desktop_config.json
claude mcp add-from-claude-desktop --scope user
```

### 2.4 Server name rules (CLI validation)

Source: GitHub issue [anthropics/claude-code#48758](https://github.com/anthropics/claude-code/issues/48758) and `support.claude.com`.

- **Regex:** `^[a-zA-Z0-9_-]{1,64}$`
- Allowed: lowercase, uppercase, digits, underscore `_`, hyphen `-`
- **Forbidden characters:** dot `.`, slash `/`, colon `:`, space
- **Max length:** 64 chars (but see §5 for the practical limit driven by tool-name math)
- **Reserved name:** `workspace` — server is skipped at load with a warning if you use it

For RvtMcp, recommended names: `rvt-mcp` (7 chars) or `revit-mcp` (9 chars). Both leave generous budget for tool names (see §5).

---

## 3. Claude Code VS Code Extension

Source: `code.claude.com/docs/en/vs-code` and ClaudeWorld 2026-03 release notes.

The extension is **a thin UI over the CLI**, not a separate MCP runtime:

- It reads/writes the same `~/.claude.json` and `.mcp.json` files as the CLI.
- v2.1.69+ added a native `/mcp` dialog inside the chat panel.
- From the dialog you can: view all configured servers + connection status, enable/disable with a click, reconnect dropped servers, manage OAuth for HTTP/SSE servers.
- For initial registration, **still use `claude mcp add` in a terminal** — the dialog manages existing servers, it doesn't add new ones.

**Workflow for RvtMcp users on VS Code:**

1. Open a terminal in VS Code (`Ctrl+\``).
2. Run `claude mcp add rvt-mcp --scope user -- "<path-to>\\RvtMcp.Server.exe"`.
3. Open Claude Code chat panel, type `/mcp` — `rvt-mcp` appears with status.
4. Click to enable/disable or reconnect.

> ⚠ **Historical caveat:** versions before ~2.1.69 (early 2026) could not list MCP servers in the extension UI at all. If `/mcp` doesn't show anything, upgrade the extension.

---

## 4. Claude Desktop

Source: `support.claude.com/en/articles/10949351` and `modelcontextprotocol.io/docs/develop/connect-local-servers`.

### 4.1 Config file location

| OS | Path |
|---|---|
| Windows | `%APPDATA%\Claude\claude_desktop_config.json` |
| macOS | `~/Library/Application Support/Claude/claude_desktop_config.json` |

### 4.2 Editing

- Open Claude Desktop → Settings → "Developer" tab → click "Edit Config" — this opens the file in your OS default editor.
- After editing, **fully quit and relaunch Claude Desktop** (Settings → quit, or `Cmd/Ctrl+Q`) — Claude Desktop does NOT hot-reload MCP config the way Claude Code does.

### 4.3 Config format

```json
{
  "mcpServers": {
    "rvt-mcp": {
      "type": "stdio",
      "command": "D:\\Projects\\bimwright\\rvt-mcp\\src\\server\\bin\\Debug\\net8.0\\RvtMcp.Server.exe",
      "args": [],
      "env": {
        "BIMWRIGHT_READ_ONLY": "0"
      }
    }
  }
}
```

Notes:
- `"type"` defaults to `"stdio"` when `"command"` is set, so it can be omitted — including it is more explicit.
- On Windows, **use double backslashes** in the path (JSON-escape) or forward slashes.
- `env` is optional. RvtMcp reads `BIMWRIGHT_*` vars (see project README).
- To run multiple Revit years simultaneously, add multiple entries each with `args: ["--target", "R24"]` etc.

---

## 5. Protocol-level rules that affect tool discoverability

These rules apply identically to **all three clients** — they come from Claude Code / Claude Desktop runtimes, not from any particular config file.

### 5.1 Tool name format Claude sees

Claude does **not** see the bare tool name your server exposes. It sees a prefixed version:

```
mcp__<server-name>__<tool-name>
```

Example: server registered as `rvt-mcp`, server's `revit_create_grid` tool → Claude sees `mcp__rvt-mcp__revit_create_grid`.

Source: `code.claude.com/docs/en/mcp` (search `mcp__` in page) and [anthropics/claude-code#18763](https://github.com/anthropics/claude-code/issues/18763).

### 5.2 Hard 64-character limit on full prefixed name

Source: [anthropics/claude-code#21050](https://github.com/anthropics/claude-code/issues/21050), [github-mcp-server#520](https://github.com/github/github-mcp-server/issues/520), [chrome-devtools-mcp#1169](https://github.com/ChromeDevTools/chrome-devtools-mcp/issues/1169).

The full string `mcp__<server-name>__<tool-name>` must be **≤ 64 characters**. Exceeding it causes Claude to reject the tool — you get "claude failed to invoke" errors with no clear cause.

**Budget math** (4 chars `mcp_` + server name + 2 chars `__`):

| Server name | Overhead | Max usable tool-name length |
|---|---|---|
| `bimwright-rvt-r22` (17) | 23 | **41** chars |
| `bimwright-rvt` (13) | 19 | **45** chars |
| `rvt-mcp` (7) | 13 | **51** chars |
| `revit-mcp` (9) | 15 | **49** chars |
| `rvt` (3) | 9 | **55** chars |

For RvtMcp v0.5+ (all tools prefixed with `revit_`): longest tool is `revit_measure_distance_between_elements` (39 chars). Full prefixed name `mcp__rvt-mcp__revit_measure_distance_between_elements` = 53 chars — comfortably under the 64-char ceiling.

### 5.3 Tool Search — the *critical* default behavior

Source: `code.claude.com/docs/en/mcp` §"Scale with MCP Tool Search".

**Tool Search is ON by default** (Sonnet 4+, Opus 4+; not supported on Haiku, partially supported on Vertex AI).

What this changes from the naïve mental model:

- At session start, Claude **only loads tool NAMES**, not descriptions, not parameter schemas. This keeps the context window small even with hundreds of MCP tools.
- When the user asks something, Claude calls an internal `ToolSearch` tool to find relevant tools. The search ranks based on:
  1. **Server `instructions` field** (a 2-KB-max free-text description the MCP server sends in its `initialize` response).
  2. **Tool name** (the literal identifier after the `mcp__server__` prefix).
- Only tools `ToolSearch` selects enter the conversation context with full description + schema.

**Implication for RvtMcp — addressed in v0.5:**

Pre-v0.5, RvtMcp had this exact failure: 226 tools exposed but Tool Search returned nothing because (a) `instructions` field was empty and (b) tool names like `create_grid`/`capture_view_image` didn't contain "revit". Agents would say "I don't have any Revit tools" despite a healthy connection.

v0.5 ships both fixes:

1. **Server `instructions` populated** at `src/server/Program.cs` `ConfigureMcpServerOptions()` — ~2 KB of keyword-dense text leading with "rvt-mcp — Model Context Protocol gateway for Autodesk Revit 2022-2027" followed by every relevant domain term (wall, door, MEP, duct, pipe, structural, IFC, DWG, etc.) and a per-toolset tool-name index.
2. **All 226 tools prefixed with `revit_`** — `create_grid` → `revit_create_grid`, `analyze_structural_connections` → `revit_analyze_structural_connections`. Semantic search of "revit X" now lands directly on the right tool.

The official doc explicitly recommends:

> "Add clear, descriptive server instructions that explain: What category of tasks your tools handle, When Claude should search for your tools, Key capabilities your server provides."

To verify discovery is working after install:

```text
You: list every Revit-related MCP tool you have access to
```

With current defaults, Claude should expose 40 tools prefixed `mcp__rvt-mcp__revit_*`; `--toolsets all` exposes 229 (232 with adaptive bake). If the configured surface is smaller than expected, check Tool Search and the server's `--toolsets` / adaptive-bake settings.

### 5.4 Server `instructions` and tool `description` are truncated at 2 KB each

Keep both concise. Put the most discriminative keywords near the start. Anything past 2 KB is dropped.

### 5.5 Tool Search can be disabled

If you want Claude to see all tool descriptions upfront (eats context but improves discoverability without good server instructions), set:

```bash
ENABLE_TOOL_SEARCH=false claude
```

Or in `settings.json`:
```json
{ "env": { "ENABLE_TOOL_SEARCH": "false" } }
```

Other values:
- `true` — force tool search even on Vertex/proxy (default behavior)
- `auto` — load upfront if tool schemas fit in ≤10% of context, defer the rest
- `auto:N` — same with custom percent threshold

---

## 6. Output and timeout limits

Source: `code.claude.com/docs/en/mcp` §"MCP output limits and warnings".

| Knob | Default | Override |
|---|---|---|
| MCP tool output warning | 10,000 tokens | n/a |
| MCP tool output hard limit | 25,000 tokens | `MAX_MCP_OUTPUT_TOKENS=50000` env var |
| Per-tool max result size | 25,000 tokens | server sets `_meta.anthropic/maxResultSizeChars` in `tools/list` response (ceiling: 500,000 chars) |
| Startup timeout for connect | unspecified | `MCP_TIMEOUT=10000` env var (ms) |

Stdio servers like RvtMcp are **not auto-reconnected** if they crash mid-session. Only HTTP/SSE servers get exponential-backoff retry (5 attempts, 1s → 2s → 4s → 8s → 16s).

---

## 7. Quick reference card for RvtMcp users

**Install once (Claude Code CLI):**
```bash
claude mcp add rvt-mcp --scope user -- "%LOCALAPPDATA%\\RvtMcp\\server\\0.4.0\\RvtMcp.Server.exe"
claude mcp list                    # verify ✓ Connected
```

**Install once (Claude Desktop):**
1. Open `%APPDATA%\Claude\claude_desktop_config.json` via Settings → Developer → Edit Config.
2. Add the `rvt-mcp` block under `mcpServers` (see §4.3 for example).
3. Fully quit + relaunch Claude Desktop.

**Install once (VS Code extension):**
1. Open terminal in VS Code.
2. Run the same `claude mcp add` command as the CLI.
3. Reload Claude Code chat panel — `/mcp` shows the server.

**Verify the agent can find tools:**
```
You: list all Revit-related tools you have
```
If Claude lists `mcp__rvt-mcp__*` tools, discovery is working. If it says "I don't have Revit-specific tools," the server `instructions` field is likely missing — see §5.3.

**Debug a stuck connection:**
- Claude Code CLI/VS Code: `/mcp` panel shows status; click reconnect.
- Claude Desktop: quit + relaunch (no in-app reconnect).
- All clients: tail `%LOCALAPPDATA%\RvtMcp\debug.log` for plugin/server messages.

---

## 8. Sources

- [Connect Claude Code to tools via MCP](https://code.claude.com/docs/en/mcp) — primary reference for CLI + VS Code extension
- [Use Claude Code in VS Code](https://code.claude.com/docs/en/vs-code)
- [Getting Started with Local MCP Servers on Claude Desktop](https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop)
- [Connect to local MCP servers — MCP spec docs](https://modelcontextprotocol.io/docs/develop/connect-local-servers)
- [Tool naming convention discrepancy — anthropics/claude-code#18763](https://github.com/anthropics/claude-code/issues/18763)
- [64-char tool name limit — anthropics/claude-code#21050](https://github.com/anthropics/claude-code/issues/21050)
- [64-char limit conflicts with MCP spec — github-mcp-server#520](https://github.com/github/github-mcp-server/issues/520)
- [Tool name length error in practice — chrome-devtools-mcp#1169](https://github.com/ChromeDevTools/chrome-devtools-mcp/issues/1169)
- [allowedMcpServers schema validation — anthropics/claude-code#48758](https://github.com/anthropics/claude-code/issues/48758)
