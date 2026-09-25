<!-- agents-install-guide -->
<!-- mcp-name: io.github.bimwright/rvt-mcp -->

# AGENTS.md — rvt-mcp install guide for AI agents

This file is machine-readable install instructions for AI coding agents (Claude Code, Cursor, Cline, VS Code Copilot, Gemini CLI, Antigravity, OpenCode, Codex, and any other stdio MCP client). A user pointed you here because they want rvt-mcp (a Revit MCP server) wired up without hand-editing config files themselves.

**What you can do:**

- Install the client setup ZIP from GitHub Releases (`RvtMcp.Setup-*-win-x64.zip`).
- Deploy the bundled add-in into `%APPDATA%\Autodesk\Revit\Addins\<year>\` for every installed Revit year, and the bundled server to a fixed per-user path.
- Connect the MCP client(s) the user asks for, using each client's own method. The installer never edits client configs.
- Verify the handshake and roll back on failure.

**Current status — 2026-09-25:** latest published release is **v0.6.3**. Use `/repos/bimwright/rvt-mcp/releases/latest`. Do not install `v0.5.0` or earlier tags.

**What you cannot do:**

- Install Revit, manage Revit licensing, or launch Revit for the first time. If Revit 2022–2027 is not installed, stop and tell the user.
- Install from unpublished/old tags or old `RvtMcp.Setup-*.zip` URLs for v0.5.0 and earlier.
- Use NuGet as the **client** installer. `Bimwright.Rvt.Server` (0.1–0.3) is obsolete. `RvtMcp.Server` is **server-only** (no Revit add-in). Only run `dotnet tool install -g RvtMcp.Server` if the user explicitly asked for a global .NET tool **and** plugins are already installed from the GitHub Release ZIP.
- Install the .NET 8 SDK, clone the repo, restore NuGet packages, or build source for a **client** install. If the user did not explicitly ask for a developer setup, use the GitHub Release ZIP only.

---

## Rules for agents

**Read these before touching anything. They exist so rvt-mcp stays predictable, auditable, and reversible.**

1. **Preview every change.** Use `-WhatIf`, `--dry-run`, or a printed diff before any write. Tell the user the exact file path and the exact change.
2. **Install from the latest GitHub Release ZIP only** for a Revit client machine. Do not fall back to `dotnet tool install`, v0.5.0 or earlier tags, source build, or repo clone unless the user explicitly asks for developer installation. If they explicitly asked for the NuGet global tool: `RvtMcp.Server` 0.6.1+, never `Bimwright.Rvt.Server`, and they still need the ZIP for plugins.
3. **Two explicit approval gates — do not collapse without the user saying so:**
   - Before running `install.ps1` without `-WhatIf`.
   - Before editing any MCP client config (show the exact command or diff first).
4. **Never bypass the Revit undo stack at runtime.** rvt-mcp's design guarantee is that every edit is reviewable and reversible. Don't advise users to work around transaction wrapping or disable `batch_execute` safety.
5. **On any failure, verify rollback.** The installer restores the previous add-ins and server on any caught error and reports retained `.rvtmcp-rollback-*` backups if recovery fails. It never edits MCP client configs, so back up a client config yourself before editing it. Do not use full uninstall as an upgrade rollback: upgrades replace add-ins and server in place. Personal data is only removed with `-Purge`.
6. **Verify before claiming done.** After connecting a client, run `tools/list` in it and confirm the single `rvt-mcp` entry responds, then call `revit_get_current_view_info` with no args.

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

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force
```

If `/releases/latest` 404s, the tag is older than v0.6.1, or the setup asset is missing, stop. Do not clone, build, or install the .NET SDK for a client machine. Do not install v0.5.0 or earlier ZIPs.

---

## Step 2 — Preview, then install

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

The installer:

- detects installed Revit years (a year counts when its `Revit.exe` exists);
- removes older add-in copies that carry RvtMcp's AddInId (Bimwright-era leftovers);
- installs the matching add-ins and the server, and checks that the server starts;
- verifies the installed add-ins against the package.

Any error restores the previous add-ins and server. A machine-wide copy under `%ProgramData%` stops the install before anything changes (removing it needs admin rights). The installer **does not configure MCP clients** — that is Step 3.

Read the summary:

- `Server :` is the command for Step 3. Since the fixed-path installer this is `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe`; v0.6.2 and earlier printed a versioned folder.
- `In use :` lists old server copies that an open MCP client still runs. Restart that client.
- `Legacy :` lists versioned copies from older installers. Repoint clients (Step 3), then run `install.ps1 -PruneOldServers`.

**Updates.** Close Revit, then run the new ZIP's installer the same way, without uninstalling first. The server path stays the same, so MCP clients only need a restart.

`-Client` is deprecated and only prints a warning.

---

## Step 3 — Connect the MCP client

The installer never edits client configs. Connect the client(s) the user asks for, using that client's own method: its `mcp add` command, settings UI or config file. **Verified per-client procedures (paths, config keys, native commands, gotchas): [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md).** The contract:

| Field | Value |
|---|---|
| Name | `rvt-mcp` (exactly one entry per client) |
| Transport | stdio |
| Command | The `Server :` path from the install summary, as an absolute path — normally `C:\Users\<user>\AppData\Local\RvtMcp\rvt\server\current\rvt-mcp.exe` |
| Args | None required. Optional flags (`--toolsets all`, `--read-only`, …) are in the README configuration table |

JSON-style clients usually take:

```json
{
  "mcpServers": {
    "rvt-mcp": {
      "command": "C:\\Users\\<user>\\AppData\\Local\\RvtMcp\\rvt\\server\\current\\rvt-mcp.exe",
      "args": []
    }
  }
}
```

**Rules:**

- **Which clients:** ask the user; do not assume all. Prefer the client's own CLI over hand-editing, and show the exact command or diff before applying it (gate 2). Register the server where every project sees it — for example the user scope, not one project folder — unless the user asks otherwise.
- **Existing `rvt-mcp` entry:** change only that entry. Keep its args and env and update only the command. If it runs a custom launcher or wrapper, ask before changing it.
- **Old paths and entries:**
  - An entry pointing at `...\RvtMcp\rvt\server\<version>\rvt-mcp.exe` (older installers) should be repointed to the `current` path. Afterwards `install.ps1 -PruneOldServers` removes the old copies.
  - Leftover `bimwright-rvt*` entries come from pre-0.5 releases. Tell the user, and remove them only with their consent.
- **Restart:** restart the client after changing its config.

---

## Step 4 — Verify

1. **List tools.** Ask the host to call `tools/list` against the wired server. Default toolsets are `query,create,view,meta` — expect `revit_get_current_view_info`, `revit_batch_execute`, and `revit_send_code_to_revit`. Clash/export/MEP need `--toolsets all` (or an explicit CSV).

2. **Handshake call.** With Revit 2022–2027 running and a model open, call `revit_get_current_view_info` with no args. A valid response looks like:

    ```json
    {
      "view_name": "Level 1",
      "view_type": "FloorPlan",
      "project_name": "Untitled"
    }
    ```

3. **Report.** Tell the user: the detected Revit year(s), the server path, each client connected and exactly what was changed there, and where any backup you made lives.

If any of these fail, **do not claim the install succeeded.** Go to rollback.

---

## Rollback

### Full uninstall

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -WhatIf    # preview what comes off
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -Yes       # apply without prompt
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -Purge     # also delete personal data (combine -KeepLogs to keep logs)
```

First remove the `rvt-mcp` entry from each client you configured; the uninstaller never touches client configs.

The uninstaller removes:

- RvtMcp add-ins for every Revit year 2022–2027, including Bimwright-era copies with the same AddInId;
- the legacy .NET global tool, if present;
- server copies, discovery files and the spill cache under `%LOCALAPPDATA%\RvtMcp\`.

A server copy that an open MCP client still runs is kept; close the client and run the uninstaller again. Everything else under `%LOCALAPPDATA%\RvtMcp\` (settings, translations, ToolBaker data, firm profiles, shared parameters, logs, captures) is kept. `-Purge` deletes the whole folder, and `-Purge -KeepLogs` keeps logs.

### Partial rollback

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Uninstall   # add-ins only (keeps server and client configs)
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `rvt-mcp.exe` path not found | Install did not complete, or the client points at an old versioned folder. | Re-run `install.ps1 -WhatIf`, then `install.ps1`; point the client at the `Server :` path from the summary. |
| `tools/list` returns 0 entries from rvt-mcp | Host not reloaded, or Revit not running. | Restart host. Launch Revit. Retry. |
| `install.ps1` fails with "Revit running" | Revit has plugin DLLs locked. | Close every Revit window, retry. |
| `install.ps1` fails with "machine-wide RvtMcp add-in" | A copy under `%ProgramData%\Autodesk\Revit\Addins\<year>\` has the same AddInId. | Remove it with admin rights, then re-run. |
| `install.ps1` fails with "Server executable could not start" | Antivirus or policy blocked `rvt-mcp.exe`. The previous install was restored. | Allow the file, then re-run. |
| Client config parse error after edit | Agent wrote invalid JSON/TOML. | Restore the backup you made, retry with a diff preview. |
| Server starts but no tools show up | Toolset filter hiding them. | Check `--toolsets` / `--read-only` flags on the host config entry. |

For anything not in this table, open an issue at <https://github.com/bimwright/rvt-mcp/issues> with the host name, Revit year, and the exact error.

---

## Fixing UI translations

The plugin UI (ribbon, toasts, History, dialogs) follows the Revit UI language, or the user's pick in the ribbon slide-out Language combo, or `BIMWRIGHT_UI_LANGUAGE` if that env var is set (it beats the user's pick at every launch). If the user reports a wrong or awkward string, you can fix it live on their machine — no reinstall, no restart:

1. **Read** `%LOCALAPPDATA%\RvtMcp\locales\_active.<locale>.json` — every key's currently displayed value, plus the `locked` list.
2. **Write** the corrected `"key": "text"` pairs into `%LOCALAPPDATA%\RvtMcp\locales\strings.<locale>.json` (create the file/folder if absent). Keep `{placeholder}` tokens identical to the English value.
3. **Verify** by re-reading `_active.<locale>.json` after ~1 second (debounced reload) — your key must show the **new value** there. `_report.<locale>.json` lists current validation problems; if your key appears under `rejected`, the `reason` tells you why (`unknown_key`, `placeholder_mismatch`, `locked_key`, …). An **absent** `_report` only means "nothing to report" — it does not prove your edit applied (an ignored wrong-locale file produces no report either). The value inside `_active` is the proof.
4. Tell the user it applied immediately via hot reload.

**Precondition:** `<locale>` must be the *active* locale — the watcher ignores override files for inactive locales, so those edits apply only when the user next switches to that language. (`en` is the fallback layer and is always live: an `en` override applies under every locale.)

To find the active locale, check `locales\` for `_active.<locale>.json` files — one exists per locale ever used and old files are **never deleted**, so existence proves nothing. The current one is the most recently written (sidecars refresh on every table swap). When in doubt, ask the user which language the ribbon Language combo shows.

Rules: `security.*` keys are locked and can never be overridden. Keys not in `_active` don't exist — don't invent new ones. Never edit files inside the plugin's install directory; only `locales\`. Full details: [docs/localization.md](docs/localization.md).

---

## Honest scope

rvt-mcp handles `revit_get_current_view_info`, `revit_batch_execute`, `revit_send_code_to_revit`, and 220+ other tools across Revit 2022–2027 when started with `--toolsets all`. The default surface is `query` + `create` + `view` + `meta` only. It does not handle installing Revit, licensing, cloud sync, or any Autodesk account operations. If the user asks for those, point them at <https://www.autodesk.com/support/revit>.

For extending the tool surface at runtime, see ToolBaker in the main [README.md](README.md).
