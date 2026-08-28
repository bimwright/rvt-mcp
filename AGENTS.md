<!-- agents-install-guide -->
<!-- mcp-name: io.github.bimwright/rvt-mcp -->

# AGENTS.md — rvt-mcp install guide for AI agents

This file is machine-readable install instructions for AI coding agents (Claude Code, Cursor, Cline, VS Code Copilot, Gemini CLI, Antigravity, OpenCode, Codex, and any other stdio MCP client). A user pointed you here because they want rvt-mcp (a Revit MCP server) wired up without hand-editing config files themselves.

**What you can do (when a GitHub Release exists):**

- Install the client setup ZIP from GitHub Releases.
- Deploy the bundled plugin into `%APPDATA%\Autodesk\Revit\Addins\<year>\` for every installed Revit year.
- Wire supported MCP hosts with the bundled self-contained server executable.
- Verify the handshake and roll back on failure.

**Current status — 2026-08-20:** there is **no published GitHub Release**. `/repos/bimwright/rvt-mcp/releases/latest` returns 404. Older tags (`v0.5.0` and earlier) have no downloadable release assets.

**What you cannot do:**

- Install Revit, manage Revit licensing, or launch Revit for the first time. If Revit 2022–2027 is not installed, stop and tell the user.
- Install from GitHub Releases, old tags, old `RvtMcp.Setup-*.zip` URLs, or NuGet (`Bimwright.Rvt.Server`, `RvtMcp.Server`).
- Install .NET 8 SDK, clone the repo, restore NuGet packages, or build source for a **client** install. If the user did not explicitly ask for a developer setup, stop and report that the client installer is unpublished until v0.6.1.

---

## Rules for agents

**Read these before touching anything. They exist so rvt-mcp stays predictable, auditable, and reversible.**

1. **Preview every change.** Use `-WhatIf`, `--dry-run`, or a printed diff before any write. Tell the user the exact file path and the exact change.
2. **Do not install from a client setup ZIP until a GitHub Release exists again.** Do not fall back to `dotnet tool install`, old tags, source build, or repo clone unless the user explicitly asks for developer installation.
3. **Two explicit approval gates — do not collapse without the user saying so:**
   - Before running `install.ps1` without `-WhatIf`.
   - Before editing any MCP host config file outside the setup installer's own preview/apply flow.
4. **Never bypass the Revit undo stack at runtime.** rvt-mcp's design guarantee is that every edit is reviewable and reversible. Don't advise users to work around transaction wrapping or disable `batch_execute` safety.
5. **On any failure, offer rollback.** Config edits are auto-backed up to `<file>.bimwright.bak`. The full stack comes off with the bundled `uninstall.ps1 -Yes`.
6. **Verify before claiming done.** After wiring, run `tools/list` in the host and confirm the single `rvt-mcp` entry responds, then call `get_current_view_info` with no args.

If the user explicitly says "skip the prompts, just install" — still do gate 1 (preview) and gate 5 (verify), but collapse gates 2 and 3 into a single upfront approval. **Never silently skip preview or verify.**

### Baked-tool routing

When the user's request may match a personal baked tool, call list_baked_tools first.
In v0.3.x baked tools never appear directly in native tools/list.
Run accepted tools through run_baked_tool name=<tool_name>.

---

## Prerequisites (check first, stop if any are missing)

| Requirement | How to check (PowerShell) | If missing |
|---|---|---|
| Windows | `[System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform('Windows')` | Stop — Revit is Windows-only. |
| Revit 2022–2027 | `Get-ChildItem 'HKLM:\SOFTWARE\Autodesk\Revit\' -ErrorAction SilentlyContinue` | Tell the user to install Revit. You cannot. |
| PowerShell ≥5.1 | `$PSVersionTable.PSVersion` | Prompt: <https://aka.ms/powershell>. |

If Revit is not running when the user first tries a tool call, that's fine — the server only needs Revit alive at tool-call time, not at install time.

---

## Step 1 — Download the client setup ZIP

**Stop. There is no published GitHub Release.** Do not run the snippet below, do not fetch older tags, and do not `dotnet tool install` `Bimwright.Rvt.Server` or `RvtMcp.Server`.

Tell the user: rvt-mcp unpublished its GitHub Releases so stale v0.5.0 and earlier installers are not offered. A v0.6.1 setup ZIP is not published yet. For a **client** machine, wait. Only clone/build if they explicitly asked for a developer install.

When a release exists again, the download looks like this:

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force
```

If `/releases/latest` 404s or the setup asset is missing, stop. Do not clone, build, or install the .NET SDK for a client machine.

---

## Step 2 — Preview, then install

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

The installer detects Revit years, installs all matching plugin ZIPs, copies the bundled server to `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\`, and wires detected Codex/OpenCode/Claude configs with one auto-detect entry named `rvt-mcp`.

Use `-Client codex`, `-Client opencode`, `-Client claude`, or `-Client none` when the user wants a specific config behavior.

---

## Step 3 — Wire the MCP host

Pick the host the user is actually running. The default wiring is one MCP entry named `rvt-mcp`; the server auto-detects the running Revit instance. The installer still deploys plugins for every detected Revit year:

```powershell
$years = Get-ChildItem 'HKLM:\SOFTWARE\Autodesk\Revit\' -ErrorAction SilentlyContinue |
         ForEach-Object { if ($_.PSChildName -match '^(\d{4})$' -and [int]$Matches[1] -ge 2022 -and [int]$Matches[1] -le 2027) { $Matches[1] } }
```

### Canonical snippet (7 of 9 hosts)

Most hosts use `{ "mcpServers": { ... } }` with this per-server shape:

```json
{
  "mcpServers": {
    "rvt-mcp": {
      "command": "%LOCALAPPDATA%\\RvtMcp\\rvt\\server\\<version>\\rvt-mcp.exe",
      "args": []
    }
  }
}
```

When hand-editing, expand `%LOCALAPPDATA%` to the real absolute path. Do not leave environment-variable placeholders in the config unless the host explicitly supports expansion.

Emit exactly one entry. Preserve every non-rvt-mcp entry already in the config — merge, don't replace. If multiple Revit versions are running, use the `switch_target` MCP tool instead of creating separate host entries.

Prefer the installer-generated absolute-path entry.

---

### 3.a — Claude Code CLI

**Config paths (pick one):**

- Project-level: `.mcp.json` in the user's current project root.
- User-level: `%USERPROFILE%\.claude.json` (merges into every project).

**Schema:** canonical `mcpServers`.

**Scripted alternative:**

```powershell
claude mcp add rvt-mcp "%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\rvt-mcp.exe"
```

Notes:

- Claude Code users historically paste JSON manually — the scripted `claude mcp add` command is the cleaner path when available.
- Don't overwrite `.mcp.json` if the user has other MCP servers in it. Merge in place.

---

### 3.b — Claude Desktop

**Config path:** `%APPDATA%\Claude\claude_desktop_config.json`.

**Schema:** canonical `mcpServers`.

**Preview required.** Read the file, show user the diff, then write. Back up to `claude_desktop_config.json.bimwright.bak` first.

**Restart required.** Claude Desktop reloads MCP config on app restart. Tell the user to quit and relaunch.

---

### 3.c — Cursor

**Config paths (pick one):**

- Project-level: `.cursor/mcp.json` in the user's current project root.
- User-level: `%USERPROFILE%\.cursor\mcp.json`.

**Schema:** canonical `mcpServers`.

**Restart recommended.** Cursor usually picks up changes on chat reload, but a full restart is safest.

---

### 3.d — Cline (VS Code extension)

**Config path:** `%APPDATA%\Code\User\globalStorage\saoudrizwan.claude-dev\cline_mcp_settings.json`.

**Schema:** canonical `mcpServers`.

**Scripted alternative:** Click "Configure MCP Servers" in the Cline pane — it opens this file directly. If the extension is not installed, prompt the user to install it from the VS Code marketplace.

---

### 3.e — VS Code Copilot (native MCP)

**⚠ Different schema.** VS Code Copilot uses `servers` (not `mcpServers`) and requires `"type": "stdio"`:

```json
{
  "servers": {
    "rvt-mcp": {
      "type": "stdio",
      "command": "%LOCALAPPDATA%\\RvtMcp\\rvt\\server\\<version>\\rvt-mcp.exe",
      "args": []
    }
  }
}
```

**Config paths (pick one):**

- Workspace: `.vscode/mcp.json` in the user's current project root.
- User-level: `mcp.json` in the VS Code user profile — open it via the command palette action **MCP: Open User Configuration**.

**Scripted alternative:** Run the command palette action **MCP: Add Server** for a guided flow.

---

### 3.f — OpenCode (scripted)

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client opencode -WhatIf   # preview
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client opencode           # apply
```

Writes to `%USERPROFILE%\.config\opencode\opencode.json`. Preserves existing entries and backs up to `opencode.json.bimwright.bak`.

Prefer the scripted path over hand-editing. If the config file doesn't exist (host not installed), the script skips gracefully.

---

### 3.g — Codex (scripted)

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client codex -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client codex
```

Writes to `%USERPROFILE%\.codex\config.toml`. Preserves existing entries and backs up to `config.toml.bimwright.bak`.

Codex uses TOML, not JSON — do not hand-edit unless you know TOML-array-of-tables. The scripted path handles the syntax.

---

### 3.h — Gemini CLI

**Scripted (preferred):**

```powershell
gemini mcp add rvt-mcp "%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\rvt-mcp.exe"
```

**Hand-edit fallback — config path:** `%USERPROFILE%\.gemini\settings.json`.

**Schema:** canonical `mcpServers`.

---

### 3.i — Antigravity (Google)

**Config path:** `%USERPROFILE%\.gemini\antigravity\mcp_config.json`.

**Schema:** canonical `mcpServers`.

**UI alternative:** Open MCP settings in Antigravity → **View raw config** → edit the JSON directly. Confirm with the user whether they prefer file-edit or UI-edit.

---

## Step 4 — Verify

1. **List tools.** Ask the host to call `tools/list` against the wired server. Default toolsets are `query,create,view,meta` — expect `revit_get_current_view_info`, `revit_batch_execute`, and `revit_send_code_to_revit`. Clash/export/MEP need `--toolsets all` (or an explicit CSV).

2. **Handshake call.** With Revit 2022–2027 running and a model open, call `get_current_view_info` with no args. A valid response looks like:

    ```json
    {
      "view_name": "Level 1",
      "view_type": "FloorPlan",
      "project_name": "Untitled"
    }
    ```

3. **Report.** Tell the user: the detected Revit year(s), the single host entry name, the host config file edited, and the `.bimwright.bak` backup location(s).

If any of these fail, **do not claim the install succeeded.** Go to rollback.

---

## Rollback

### Full uninstall (everything rvt-mcp touched)

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -WhatIf    # preview what comes off
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -Yes       # apply without prompt
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -KeepLogs  # preserve logs
```

Removes: the self-contained server, legacy .NET global tool if present, plugin DLLs for every Revit year, discovery files at `%LOCALAPPDATA%\RvtMcp\`, ToolBaker cache, and rvt-mcp entries in scanned host configs.

**Scope caveat.** `uninstall.ps1` scans known host configs (OpenCode, Codex, Claude Desktop, Claude Code user-level) but does not scan project-level `.mcp.json` files. If you edited a project `.mcp.json`, restore it from `.bimwright.bak` manually or remove the entries by hand.

### Partial rollback

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Uninstall   # plugin only (keeps server + host configs)
```

### Restore a single host config from backup

```powershell
Copy-Item 'path\to\config.ext.bimwright.bak' 'path\to\config.ext' -Force
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `rvt-mcp.exe` path not found | Setup ZIP was moved or install did not complete. | Re-run `install.ps1 -WhatIf`, then `install.ps1`; restore config from `.bimwright.bak` if needed. |
| `tools/list` returns 0 entries from rvt-mcp | Host not reloaded, or Revit not running. | Restart host. Launch Revit. Retry. |
| `install.ps1` fails with "Revit running" | Revit has plugin DLLs locked. | Close every Revit window, retry. |
| Host config parse error after edit | Agent wrote invalid JSON/TOML. | Restore from `.bimwright.bak`, retry with a diff preview. |
| Server starts but no tools show up | Toolset filter hiding them. | Check `--toolsets` / `--read-only` flags on the host config entry. |

For anything not in this table, open an issue at <https://github.com/bimwright/rvt-mcp/issues> with the host name, Revit year, and the exact error.

---

## Honest scope

rvt-mcp handles `revit_get_current_view_info`, `revit_batch_execute`, `revit_send_code_to_revit`, and 220+ other tools across Revit 2022–2027 when started with `--toolsets all`. The default surface is `query` + `create` + `view` + `meta` only. It does not handle installing Revit, licensing, cloud sync, or any Autodesk account operations. If the user asks for those, point them at <https://www.autodesk.com/support/revit>.

For extending the tool surface at runtime, see ToolBaker in the main [README.md](README.md).
