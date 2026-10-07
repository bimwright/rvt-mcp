<!-- agents-install-guide -->
<!-- mcp-name: io.github.bimwright/rvt-mcp -->

# AGENTS.md — rvt-mcp install guide for AI agents

This file is the install protocol for an AI agent with permission to run PowerShell and edit files on the user's Windows machine (for example Claude Code, Codex or Cursor). A normal chat session without local execution tools cannot perform the install: explain that limitation and offer the manual steps instead.

**Installer agent ≠ target client.** The user may ask Codex to install for Claude Desktop. Configure only the client(s) the user requests, not automatically the client running your session. If no target is specified, ask before previewing changes.

**What you can do:**

- Install the client setup ZIP from GitHub Releases (`RvtMcp.Setup-*-win-x64.zip`).
- Deploy the bundled add-in into `%APPDATA%\Autodesk\Revit\Addins\<year>\` for every installed Revit year, and the bundled server to a fixed per-user path.
- Connect the requested MCP client(s): always pass `-Client <names>` for direct registration, or `-Client none` for the Claude Desktop MCPB route. The installer itself defaults to every detected client; do not use that default unless the user requested it.
- Verify installation, target-client connection and Revit handshake separately; troubleshoot failures and verify any attempted rollback.

**Release selection:** resolve `/repos/bimwright/rvt-mcp/releases/latest` once for a normal install, or use the published release the user explicitly requested. Record its tag and use assets from that one release throughout. v1.0.0 introduced the Claude Desktop MCPB route and the `%LOCALAPPDATA%\Bimwright\rvt-mcp\` data path. Do not install `v0.5.0` or earlier tags.

**What you cannot do:**

- Install Revit, manage Revit licensing, or launch Revit for the first time. If Revit 2022–2027 is not installed, stop and tell the user.
- Install from unpublished tags or `RvtMcp.Setup-*.zip` URLs for v0.5.0 and earlier.
- Use NuGet as the **client** installer. `Bimwright.Rvt.Server` (0.1–0.3) is obsolete. `RvtMcp.Server` is **server-only** (no Revit add-in). Only run `dotnet tool install -g RvtMcp.Server` if the user explicitly asked for a global .NET tool **and** plugins are already installed from the GitHub Release ZIP.
- Install the .NET 8 SDK, clone the repo, restore NuGet packages, or build source for a **client** install. If the user did not explicitly ask for a developer setup, use the GitHub Release ZIP only.

---

## Rules for agents

**Read these before touching anything. They exist so rvt-mcp stays predictable, auditable, and reversible.**

1. **Preview every change.** Use `-WhatIf`, `--dry-run`, or a printed diff before any write. Tell the user the exact file path and the exact change.
2. **Install from the selected published GitHub Release ZIP only** for a Revit client machine. Do not fall back to `dotnet tool install`, v0.5.0 or earlier tags, source build, or repo clone unless the user explicitly asks for developer installation. If they explicitly asked for the NuGet global tool: `RvtMcp.Server` 0.6.1+, never `Bimwright.Rvt.Server`, and they still need the ZIP for plugins.
3. **Two explicit approval gates — do not collapse without the user saying so:**
   - Before running `install.ps1` without `-WhatIf`. The `-WhatIf` preview lists every client config the run will edit (its `Client :` lines) — show those to the user as part of this approval.
   - Before editing any MCP client config by hand (show the exact command or diff first).
4. **Never bypass the Revit undo stack at runtime.** rvt-mcp's design guarantee is that every edit is reviewable and reversible. Don't advise users to work around transaction wrapping or disable `batch_execute` safety.
5. **On a caught install/edit failure, verify recovery.** The installer attempts to restore the previous add-ins and server on a caught installation error and reports retained `.rvtmcp-rollback-*` backups if recovery fails. A pending restart, missing model or stopped Revit connection is not an installation rollback trigger. Client-config edits made by `-Client` are backed up to `<config>.bak` and verified by reparse — a failed write restores the backup on the spot. For hand edits, back up the client config yourself first. Do not use full uninstall as an upgrade rollback: upgrades replace add-ins and server in place. Personal data is only removed with `-Purge`.
6. **Verify before claiming done.** Report installation, target-client connection and Revit handshake separately (Step 4). When the target client's tools are unavailable to this session, leave connection/handshake **pending user verification**; never use your own client as proof for another client.

If the user explicitly says "skip the prompts, just install", still preview changes and verify the result; combine the two approval gates into one upfront approval. **Never silently skip preview or verify.**

### Baked-tool routing

When the user's request may match a personal baked tool, call list_baked_tools first.
In v0.3.x baked tools never appear directly in native tools/list.
Run accepted tools through run_baked_tool name=<tool_name>.

---

## Prerequisites (check first, stop if any are missing)

| Requirement | How to check (PowerShell) | If missing |
|---|---|---|
| Windows | `$env:OS -eq 'Windows_NT'` | Stop — Revit is Windows-only. |
| Revit 2022–2027 | Inspect `HKLM:\SOFTWARE\Autodesk\Revit\` and `%ProgramFiles%\Autodesk\Revit <year>\`; confirm an actual `Revit.exe` exists (registry keys may survive uninstall). The installer's `-WhatIf` performs detection. | Tell the user to install Revit. You cannot. |
| PowerShell ≥5.1 | `$PSVersionTable.PSVersion` | Prompt: <https://aka.ms/powershell>. |

Revit may be closed during installation and tool discovery. Revit-dependent calls require it running, with MCP started and a model open for the view-info handshake.

---

## Step 0 — Confirm target, route and existing installation

1. Confirm the target client(s). Installer names: **`claude` = Claude Code**, **`claude-desktop` = Claude Desktop**. Do not substitute one for the other.
2. For Claude Desktop, default to **Setup + direct config registration** (`-Client claude-desktop`) for agent-assisted installs. If the user requests the extension/settings UI, choose **Setup + MCPB** (`-Client none`). Use exactly one Desktop registration; do not install both routes.
3. Read [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md), especially the Claude Desktop section. Detect classic versus MSIX and inspect the active config and any existing rvt-mcp extension in Desktop's UI. If you cannot inspect the UI, ask the user whether the extension is installed. Do not guess the config path or silently remove an existing registration. With an existing MCPB, retain that route unless the user approves switching.
4. Check `%LOCALAPPDATA%\RvtMcp\` (legacy) and `%LOCALAPPDATA%\Bimwright\rvt-mcp\` (current). For upgrades, close Revit and stop clients using the old gateway before installing. If both folders exist, stop and inspect/back up the data; do not delete either or merge them blindly. See [docs/install.md](docs/install.md#upgrade).
5. Ask the user to close Revit and fully quit Claude Desktop (tray included) before Desktop config edits. If Desktop is hosting this agent, finish preparation first and hand off the quit/install/relaunch step to the user or an agent outside Desktop. Do not kill processes or elevate automatically.

## Step 1 — Download the client setup ZIP

For a normal latest-release install:

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
$base = "https://github.com/bimwright/rvt-mcp/releases/download/$tag"
Invoke-WebRequest "$base/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Invoke-WebRequest "$base/RvtMcp.Setup-$tag-win-x64.zip.sha256" -OutFile "$zip.sha256"
$expected = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or $actual -ne $expected) { throw 'Setup ZIP checksum mismatch' }
Expand-Archive $zip -DestinationPath $dir -Force
```

For an explicitly requested published release, set `$tag` to its tag instead of querying latest. Do not overwrite a previous extraction directory if it holds user files. If release lookup fails, the tag is older than v0.6.1, a required asset is missing or checksum verification fails, stop. Do not clone, build, or install the .NET SDK for a client machine. Do not install v0.5.0 or earlier ZIPs.

For MCPB, also download the selected release's `rvt-mcp-desktop-<version>.mcpb` and matching `.mcpb.sha256` assets and verify the bundle hash before opening it (`<version>` is the selected tag without its leading `v`). If either asset is missing, stop; do not substitute an older bundle. Do not mix an extension from one release with a Setup ZIP from another.

---

## Step 2 — Preview, then install

```powershell
# Claude Desktop direct-config route (default for agent-assisted installs):
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf -Client claude-desktop
# Show the preview and obtain approval before running:
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client claude-desktop
```

Use the **same options** for preview and apply. For another requested target, replace `claude-desktop` with its installer name; for MCPB use `none` in both commands. Pass a comma list only when the user requested multiple clients.

The installer:

- detects installed Revit years (a year counts when its `Revit.exe` exists);
- removes older add-in copies that carry RvtMcp's AddInId (Bimwright-era leftovers);
- installs the matching add-ins and the server, and checks that the server starts;
- seeds `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json` with `"toolsets": ["all"]` so a fresh install exposes the full tool surface — a `toolsets` key the user already set is kept, and the file stays user data (uninstall keeps it, `-Purge` removes it);
- verifies the installed add-ins against the package.

A caught installation error triggers rollback of the previous add-ins and server; check the error for any retained recovery backups. A machine-wide copy under `%ProgramData%` stops the install before anything changes (removing it needs admin rights). The same run then wires the clients selected by `-Client` — that is Step 3.

Read the summary:

- `Server :` is the command for Step 3. Since the fixed-path installer this is `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`; v0.6.2 and earlier printed a versioned folder.
- `In use :` lists old server copies that an open MCP client still runs. Restart that client.
- `Legacy :` lists versioned copies from older installers. Repoint clients (Step 3), then preview/apply `install.ps1 -PruneOldServers` with the same explicit `-Client` selection.

**Updates.** Close Revit and stop clients using the gateway, then run the new ZIP's installer with the same explicit client selection, without uninstalling first. The server path stays the same; restart the clients afterwards. For MCPB, update the matching extension too.

`-Client <names>` wires only the named clients and `-Client none` leaves client configs alone — see Step 3. `-WireClient` is a deprecated alias.

---

## Step 3 — Connect the target MCP client

### Direct registration

The Step 2 run already wired the selected client(s) using the wiring guide: detection, `.bak` backup, minimal text edits (JSONC comments survive), repointing old versioned paths, and reporting custom launchers and legacy `bimwright-rvt*` entries without replacing them.

`Client  :` lines in the summary report what happened per client (wired / already / repointed / custom launcher kept / not detected). Check the **requested target's** line. If it is missing or reports skipped/not detected/custom, inspect the reason and agree any additional change before proceeding; do not silently wire your own client instead. Cherry Studio has no safe file path — the installer prints a `cherrystudio://` deeplink for the user to open instead.

For Desktop, confirm the entry is in the active classic/MSIX config and survives a full quit/relaunch. The installer does not remove an existing MCPB registration. If switching routes, obtain approval and remove/disable the other registration through its supported config/UI path first.

### Claude Desktop MCPB (user-selected alternative)

After Step 2 with `-Client none`, guide the user through **Settings → Extensions → Advanced settings → Install Extension…** and select the verified `.mcpb` from Step 1. Labels can vary by Desktop build. This is a user UI step unless the agent has an authorized UI tool; never edit Claude's private extension storage. If custom extensions are blocked by organizational policy, report it rather than bypassing it.

Before enabling the extension, inspect and obtain approval to remove/disable any previous manual rvt-mcp entry. `-Client none` preserves existing entries; it does not remove duplicates. The extension launches the separately installed gateway and requires its exact matching executable hash; check the selected release's notes for signing status. `rvt_mcp_setup_status` means setup is missing or mismatched, not a successful Revit connection. Upgrade Setup and MCPB together; see [docs/install.md#claude-desktop-mcpb](docs/install.md#claude-desktop-mcpb).

If the requested target is also hosting this session, plan the restart/handoff last. Verify the target in the next session, not in the installer agent's unrelated client.

Manual path (clients the installer does not know, or when the user wants hand control): use that client's own `mcp add` command, settings UI or config file. **Verified per-client procedures (paths, config keys, native commands, gotchas): [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md).** The contract:

| Field | Value |
|---|---|
| Name | `rvt-mcp` (exactly one entry per client) |
| Transport | stdio |
| Command | The `Server :` path from the install summary, as an absolute path — normally `C:\Users\<user>\AppData\Local\Bimwright\rvt-mcp\server\current\rvt-mcp.exe` |
| Args | None required — an installed machine already enables the full surface via `rvtmcp.config.json`. Optional flags (`--toolsets`, `--read-only`, …) are in the README configuration table |

JSON-style clients usually take:

```json
{
  "mcpServers": {
    "rvt-mcp": {
      "command": "C:\\Users\\<user>\\AppData\\Local\\Bimwright\\rvt-mcp\\server\\current\\rvt-mcp.exe",
      "args": []
    }
  }
}
```

**Rules:**

- **Which clients:** pass `-Client <requested names>` (or `-Client none` for MCPB); only use `auto`/`all` when the user requests every detected client. For hand wiring, prefer the client's own CLI over editing files, and show the exact command or diff before applying it (the separate manual-config approval gate). Register the server where every project sees it — for example the user scope, not one project folder — unless the user asks otherwise.
- **Existing `rvt-mcp` entry:** change only that entry. Keep its args and env and update only the command. If it runs a custom launcher or wrapper, ask before changing it.
- **Old paths and entries:**
  - An entry pointing at `...\RvtMcp\rvt\server\<version>\rvt-mcp.exe` (older installers) should be repointed to the `current` path. Afterwards `install.ps1 -PruneOldServers` with the same explicit `-Client` selection removes the old copies; preview before applying.
  - Leftover `bimwright-rvt*` entries come from pre-0.5 releases. Tell the user, and remove them only with their consent.
- **Restart:** restart the client after changing its config.
- **Wiring your own client:** if your session runs inside the client being configured, see [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md) §"If you are the agent running inside the client being wired" — tools only appear after a restart (which ends this session), the running app may overwrite the config on exit, and verification happens in the next session.

---

## Step 4 — Verify in three separate stages

1. **Installed.** Check the install summary, server path and selected add-in verification. This proves files were installed, not that Desktop connected or that Revit is reachable.
2. **Target client connected.** Fully quit/relaunch the requested client and confirm exactly one registration for this gateway. In that client, check that `tools/list` includes `revit_get_current_view_info`. Revit does **not** need to be running for the gateway's tool list. A file that parses, or tools visible in a different client, is not a target-client connection check. Preserve the user's toolsets/read-only choices; do not enable write tools just to match a catalog count.
3. **Revit handshake verified.** Ask the user to launch Revit with the installed add-in, open a model and start MCP from **Add-Ins → RvtMcp**. In the target client, call `revit_get_current_view_info` with no args and confirm a successful response identifying the actual open model/view.

If your session cannot access the target client's tools, hand the user this verification prompt after restart:

```text
Check that rvt-mcp is connected in this client and revit_get_current_view_info is available.
Then call it with no arguments and report the open Revit model and view. Do not modify the model.
```

**Report each stage as verified, failed or pending**, with the selected release, Revit years, route, server path, changed config path(s), backup locations and the next user action. If Revit is closed, MCP has not been started, a model is missing, or a restart/UI action is still needed, leave that stage pending; do not claim end-to-end success or uninstall a valid installation.

For an actual failure, troubleshoot the failing stage first. Check installer rollback on a caught install error; restore a client-config backup only when that edit failed and the backup is still applicable. A runtime connection failure is not a reason to run full uninstall or delete personal data.

---

## Recovery and uninstall

A caught install/edit failure should restore the previous files/config as described above; inspect its recovery report before taking further action. The commands below are removal options, **not** upgrade rollback. Use them only when the user requests removal, preview first and obtain explicit consent before deleting personal data with `-Purge`. For MCPB, disabling/removing the extension is a separate Desktop UI action and does not remove gateway files.

### Full uninstall

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -WhatIf    # preview what comes off
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -Yes       # apply without prompt
powershell -ExecutionPolicy Bypass -File "$dir\uninstall.ps1" -Purge     # also delete personal data (combine -KeepLogs to keep logs)
```

First remove the `rvt-mcp` entry from each client you configured — `install.ps1 -Uninstall -Client <names>` does it for supported clients; the uninstaller itself never touches client configs.

The uninstaller removes:

- RvtMcp add-ins for every Revit year 2022–2027, including Bimwright-era copies with the same AddInId;
- the legacy .NET global tool, if present;
- server copies, both legacy `revit-YYYY.json` and per-instance `revit-YYYY-PID.json` discovery files, and the spill cache under `%LOCALAPPDATA%\Bimwright\rvt-mcp\`; a leftover `%LOCALAPPDATA%\RvtMcp\` gets the same sweep, regardless of which version was installed.

A server copy that an open MCP client still runs is kept; close the client and run the uninstaller again. Everything else under `%LOCALAPPDATA%\Bimwright\rvt-mcp\` (settings, translations, ToolBaker data, firm profiles, shared parameters, logs, captures) is kept. `-Purge` deletes the whole folder, and `-Purge -KeepLogs` keeps logs.

### Add-ins-only removal

```powershell
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Uninstall   # add-ins only (keeps server and client configs)
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `rvt-mcp.exe` path not found | Install did not complete, or the client points at an old versioned folder. | Re-run preview/install with the same explicit `-Client` selection; point the client at the `Server :` path from the summary. |
| Target client has no `revit_*` tools | Client not reloaded, wrong config path, server launch failure, duplicate registration or filtering. | Fully quit/relaunch the target; check its active config/extension and MCP logs. Revit is not required for `tools/list`. |
| `revit_get_current_view_info` cannot reach Revit | Revit closed, MCP stopped, model missing or server/add-in mismatch. | Open a model, start MCP from Add-Ins → RvtMcp, and check gateway/add-in pairing. Keep installation and client-connection results separate. |
| MCPB exposes `rvt_mcp_setup_status` | Matching gateway is missing or its executable hash differs. | Install Setup and MCPB from the same release, then restart Revit and the connection. Do not use a PATH/global-tool fallback. |
| `install.ps1` fails with "Revit running" | Revit has plugin DLLs locked. | Close every Revit window, retry. |
| `install.ps1` fails with "machine-wide RvtMcp add-in" | A copy under `%ProgramData%\Autodesk\Revit\Addins\<year>\` has the same AddInId. | Remove it with admin rights, then re-run. |
| `install.ps1` fails with "Server executable could not start" | Antivirus or policy blocked `rvt-mcp.exe`. The previous install was restored. | Allow the file, then re-run. |
| Client config parse error after edit | Agent wrote invalid JSON/TOML. | Restore the backup you made, retry with a diff preview. |
| Server starts but no tools show up | Toolset filter hiding them. | Check `--toolsets` / `--read-only` flags on the host config entry. |

For anything not in this table, open an issue at <https://github.com/bimwright/rvt-mcp/issues> with the host name, Revit year, and the exact error.

---

## Fixing UI translations

The plugin UI (ribbon, toasts, History, dialogs) follows the Revit UI language, or the user's pick in Settings → Language (ribbon slide-out → **Language** button), or `BIMWRIGHT_UI_LANGUAGE` if that env var is set (it beats the user's pick at every launch). If the user reports a wrong or awkward string, you can fix it live on their machine — no reinstall, no restart:

1. **Read** `%LOCALAPPDATA%\Bimwright\rvt-mcp\locales\_active.<locale>.json` — every key's currently displayed value, plus the `locked` list.
2. **Write** the corrected `"key": "text"` pairs into `%LOCALAPPDATA%\Bimwright\rvt-mcp\locales\strings.<locale>.json` (create the file/folder if absent). Keep `{placeholder}` tokens identical to the English value.
3. **Verify** by re-reading `_active.<locale>.json` after ~1 second (debounced reload) — your key must show the **new value** there. `_report.<locale>.json` lists current validation problems; if your key appears under `rejected`, the `reason` tells you why (`unknown_key`, `placeholder_mismatch`, `locked_key`, …). An **absent** `_report` only means "nothing to report" — it does not prove your edit applied (an ignored wrong-locale file produces no report either). The value inside `_active` is the proof.
4. Tell the user it applied immediately via hot reload.

**Precondition:** `<locale>` must be the *active* locale — the watcher ignores override files for inactive locales, so those edits apply only when the user next switches to that language. (`en` is the fallback layer and is always live: an `en` override applies under every locale.)

To find the active locale, check `locales\` for `_active.<locale>.json` files — one exists per locale ever used and old files are **never deleted**, so existence proves nothing. The current one is the most recently written (sidecars refresh on every table swap). When in doubt, ask the user which language Settings → Language shows.

Rules: `security.*` keys are locked and can never be overridden. Keys not in `_active` don't exist — don't invent new ones. Never edit files inside the plugin's install directory; only `locales\`. Full details: [docs/localization.md](docs/localization.md).

---

## Honest scope

rvt-mcp handles `revit_get_current_view_info`, `revit_batch_execute`, `revit_send_code_to_revit`, and 220+ other tools across Revit 2022–2027. Machines installed via `install.ps1` get the full surface by default (the installer seeds `toolsets=all` unless the user set their own list); a bare `rvt-mcp.exe` without that config defaults to `query` + `create` + `view` + `meta` only. It does not handle installing Revit, licensing, cloud sync, or any Autodesk account operations. If the user asks for those, point them at <https://www.autodesk.com/support/revit>.

For extending the tool surface at runtime, see ToolBaker in the main [README.md](README.md).
