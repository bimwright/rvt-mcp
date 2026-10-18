# Install, upgrade and uninstall

The quick path is in the [README](../README.md#install). This page has the details: what the installer does, upgrades, uninstall, developer and NuGet installs, and migration from `Bimwright.Rvt.*`.

## What the installer does

Client machines should use the setup ZIP from [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest). It bundles a self-contained MCP server and Revit 2022–2027 plugins — no .NET SDK, NuGet global tool, or source clone. AI agents follow [AGENTS.md](../AGENTS.md).

The installer detects Revit 2022–2027 (a year counts when its `Revit.exe` exists), installs the matching add-ins and the server at the fixed path `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`, checks that the server starts and verifies the add-ins against the package. It also seeds `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json` with `"toolsets": ["all"]` when the file doesn't already set `toolsets` — so a fresh install exposes the full tool surface, while your own `toolsets` choice survives upgrades (a bare `rvt-mcp.exe` without the file still defaults to `query,create,view,meta`).

Without `-Client`, it wires every MCP client it detects. **For agent-assisted installs, explicitly select only the user's requested target(s):** `claude` means Claude Code, **`claude-desktop` means Claude Desktop**. The installer agent may run in a different client and need not configure itself. `-Client none` leaves client configs untouched, `-WhatIf` previews, and `-Uninstall -Client <names>` removes just the selected client's `rvt-mcp` entry. It applies [mcp-client-wiring.md](mcp-client-wiring.md): `.bak` backup before each edit, minimal text edits that keep JSONC comments, repointing old versioned paths, and reporting (never replacing) custom launchers and legacy `bimwright-rvt*` entries.

Do **not** install v0.5.0 or earlier ZIPs. Do **not** `dotnet tool install -g Bimwright.Rvt.Server` (legacy 0.1–0.3). Do **not** use NuGet instead of this ZIP on a Revit client machine — the tool package has no add-in.

On a fresh machine, follow the [README verification steps](../README.md#install) after installing and registering the MCP client.

## Claude Desktop: choose one registration route

**Default for agent-assisted installs: direct config.** Fully quit Desktop (tray included) and close Revit, then preview `install.ps1 -WhatIf -Client claude-desktop` and apply `install.ps1 -Client claude-desktop` after approval. The installer supports classic and MSIX config paths; see [Desktop wiring](mcp-client-wiring.md#claude-desktop-file). Restart Desktop before testing. No `.mcpb` is needed for this route.

**Alternative: MCPB extension.** Choose it when the user wants Desktop's extension/settings UI. Use `-Client none` and the procedure below. Existing registrations must be inspected first; switching routes requires consent to remove/disable the old registration. Do not enable both for the same gateway.

## Claude Desktop MCPB

Choose one published release and use its Setup ZIP and `rvt-mcp-desktop-<version>.mcpb` (`<version>` is the release tag without its leading `v`). Verify both against their SHA-256 sidecars. If the bundle or checksum is missing, stop rather than substituting an older extension. The extension does not install Revit, the gateway or its add-ins; check that release's notes for signing status.

1. Close Revit and stop clients using the gateway. Preview the matching installer
   with `install.ps1 -WhatIf -Client none`, then install with `install.ps1 -Client none`.
   To wire other clients, name only those clients with `-Client`; leave Claude Desktop
   to the extension. Existing Desktop config entries are preserved, so remove or
   disable a previous manual `rvt-mcp` registration before enabling the extension.
2. In Claude Desktop, open **Settings → Extensions → Advanced settings → Install Extension…**
   and select the verified `.mcpb`. Labels may vary by Desktop build. Ask the user to
   perform this step unless the agent has an authorized UI tool; do not edit private
   extension storage or bypass organizational policy blocking custom extensions.
   Use one registration for this gateway and connect/reconnect before verification.
3. The launcher checks the executable SHA-256 against the server in the matching
   Setup ZIP. Its default path is
   `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`.
   An override must point to those same executable bytes. Missing or mismatched
   files stop with `rvt_mcp_setup_status`; there is no PATH/global-tool fallback.
   This verifies the server file, not the add-in already loaded by Revit: update
   the gateway and add-ins together and restart Revit.
4. Defaults are all toolsets, full mode, send_code enabled, call logging disabled
   and response guard enabled. The extension also exposes a target selector:
   `auto`, a 4-digit Revit year, `pid:<n>` or `id:revit-<year>-<pid>`, plus optional
   response-size thresholds. Empty values inherit server configuration; explicit
   send_code, call-log and response-guard choices pass their CLI on/off flags.
   Unchecked read-only sends no override, so read-only set elsewhere still applies.
   Read-only always removes arbitrary code and baked-tool execution.
5. Tools and prompts are discovered on connection. Upgrade the Setup and extension
   together; a new server build needs the matching extension. Removing the
   extension does not uninstall the gateway, add-ins or personal data.

`install.ps1 -Client none` updates Setup files only; it does not update the
Desktop extension. If an old extension reports `rvt_mcp_setup_status` after a
newer Setup was installed, the version in that message is the old extension's
own expectation. Update the extension to the selected newer release; do not
downgrade a verified newer gateway just to satisfy the old extension's message.

## Verify and hand off

Report these stages separately:

1. **Installed:** the installer completed and verified server/add-in files.
2. **Claude Desktop connected:** after a full quit/relaunch, the selected config/extension route connects and `tools/list` includes `revit_get_current_view_info`. Revit need not be running for tool discovery. An agent in another client cannot prove Desktop's connection by testing its own tools.
3. **Revit handshake verified:** open a model in Revit, start MCP from **Add-Ins → RvtMcp**, and call `revit_get_current_view_info` with no args in Desktop. Confirm it identifies the actual open model/view.

If a user UI action, restart or model is still needed, report that stage as **pending**, not a completed end-to-end install. If the installer agent cannot access Desktop tools, give the user a prompt to call `revit_get_current_view_info` after restart and report the result. Troubleshoot the failing stage; do not uninstall valid files or purge personal data merely because Revit is closed or the client has not reloaded.

## Upgrade

v1.0.x and v1.1.0 already use `%LOCALAPPDATA%\Bimwright\rvt-mcp\`. Upgrade that installation in place with the server and add-ins from one Setup ZIP. Older installers used `%LOCALAPPDATA%\RvtMcp\`; the new installer moves that whole folder to the current root, flattens `rvt\server\` to `server\`, and preserves settings, ToolBaker data and other personal files. Close Revit and MCP clients using rvt-mcp before this migration. Known client commands are repointed only after the installed server and all selected add-ins pass verification; a caught failure restores the old folder and leaves those commands unchanged. If both product folders already exist without a completed migration marker, the installer stops without merging them. A completed `.migrated-from-rvtmcp-v2` marker keeps the current profile and the legacy recovery files.

Development runtimes also migrate legacy user data before loading configuration, so
starting a newer server/add-in without the installer does not silently select an empty
profile. They copy settings, ToolBaker data and known user-data folders from `RvtMcp`
or the older shared `Bimwright` root, preserving sources. SQLite uses a consistent
backup including committed WAL content. Partial copies can resume; differing existing
files cause `MIGRATION_REQUIRED` and stop startup before fallback settings are loaded.
Resolve the legacy/current conflict from backups before retrying. Known diagnostic
`.log` files are excluded from runtime copying. Background maintenance
retains a 64 KiB tail of each known legacy diagnostic file and uses the bounded shared
`rvt-mcp.log` for new diagnostics. Generated record logs have a 30-day / 100 MiB
retention limit per group; settings, models and databases are preserved.
Completion markers are written only after successful migration.

Runtime security settings combine with the Revit add-in's settings: read-only applies
if either side enables it; `send_code` requires both sides to allow it. Client flags
cannot weaken the policy configured in Revit.

Updates are manual. Upgrade in place; do not uninstall first. Use the Setup ZIP
and MCPB from the same release and follow this sequence:

1. Close all Revit windows and fully quit clients using the gateway, including
   Claude Desktop in the tray. Extract the new Setup ZIP into a separate folder.
2. Back up `%LOCALAPPDATA%\Bimwright\rvt-mcp\` while the gateway is stopped. If
   `%LOCALAPPDATA%\RvtMcp\` still exists, back it up too. If both roots contain
   data, preserve both and resolve the [migration conflict](#upgrade-troubleshooting)
   before retrying installation. Do not use `-Purge` during an upgrade.
3. Preview and apply with the same explicit client selection. For Desktop direct
   config, use `install.ps1 -WhatIf -Client claude-desktop`, then
   `install.ps1 -Client claude-desktop`. For an existing MCPB installation, use
   `install.ps1 -WhatIf -Client none`, then `install.ps1 -Client none`; keep the
   extension route and avoid adding a second Desktop registration.
4. For MCPB, install the matching new `.mcpb` through Desktop's Extensions UI.
   The extension ID remains `rvt-mcp-desktop`. Check that Desktop shows the
   selected release version, then review `toolsets`, `read_only`, `send_code`,
   `target`, `server_path` and permission choices. An override must point to the
   new Setup's gateway. Settings and permission retention are managed by Desktop;
   do not assume that previous choices or "Always allow" survived the update.
5. Restart Revit and the target client, repeat the [verification stages](#verify-and-hand-off),
   and check that expected baked tools appear in `revit_list_baked_tools` before
   running them. Read-only mode intentionally hides baked-tool execution.

**ToolBaker data is kept.** The installer replaces the server and add-ins while
preserving the product data root, including `bake.db` and its SQLite sidecars,
`baked\`, `bake-audit.jsonl`, settings and other personal files. Legacy-root
relocation moves the whole tree; a caught installation failure attempts rollback
and reports any recovery failure. Older `baked\registry.json` entries are imported
without deleting the original files or replacing existing registry names. Invalid
entries or missing source files are reported in the audit log. Preserved data does
not by itself prove that every old tool runs successfully in the selected Revit.

The `server\current` command path stays the same across upgrades. A running client can still hold the previous gateway process, instructions and tool catalog: restart/reconnect it and verify the new connection before continuing. The summary lists retained running copies under `In use`; the next install removes them after they are free. For MCPB this stable path is not a compatibility guarantee: the extension's exact hash check still requires a matching extension update. Older add-in copies that carry RvtMcp's AddInId — Bimwright-era leftovers — are removed automatically, because Revit would otherwise load only one of them. A machine-wide copy under `%ProgramData%` stops the install (removing it needs admin rights).

Before replacing files, the installer checks package checksums when a manifest is present, validates every selected plugin ZIP (including its add-in manifest), stages the payload and refuses installation while Revit is running. It then starts the new server once (`--help`) and verifies the installed add-ins byte for byte against the package. Any caught error restores the previous add-ins and server. If rollback is blocked by file locks or permissions, the error identifies retained `.rvtmcp-rollback-*` backups; hard termination or power loss requires manual recovery.

Upgrading from v0.6.2 or earlier: those installers put the server in a versioned folder (`...\rvt\server\0.6.2\`). The summary lists such folders under `Legacy`; point your clients at the new `current` path, then preview/apply `install.ps1 -PruneOldServers` with the same explicit `-Client` selection to remove the old copies.

### Verify an upgrade from v1.0.x or v1.1.0

Development builds after v1.1.0 provide `revit_health` and clearer protocol-pairing refusals. Reinstalling the published v1.1.0 package does not add those changes; use the instructions and assets for the release being installed.

1. Fully restart Revit and reconnect/restart the requested MCP client. Check its fresh `initialize` serverInfo and `tools/list`, rather than an earlier conversation's cached tool list.
2. Call `revit_list_available_targets` and choose the intended instance. If several Revit windows share a year, use the listed `pid:<n>` selector in `revit_switch_target`; PID is not a new required argument on every tool.
3. On a build that provides it, call `revit_health` and compare `setup_gateway`, `setup_plugin` and `mcp.version_alignment`/`revision_alignment` with the installed release. Follow `client_verification` to compare that same connection's handshake/catalog with the observed gateway. Missing identities or inaccessible client evidence remain unverified; an installed file's version alone does not prove which add-in Revit loaded.
4. Verify the chosen instance and document with `revit_switch_target({"version":"pid:<listed-pid>","verify":true})`, then check the model/view. A matching release does not automatically confirm the document, and a health check does not authorize writes.

If the tools still show an old surface, check the client's active config, project/user scope, custom launcher and MCPB release before reconnecting again. Keep existing args/env, including read-only and toolset choices. No general MCP client upgrade is required merely to select a PID; a stale process or cached schema must be reloaded.

<a id="upgrade-troubleshooting"></a>

### Troubleshooting upgrades

For older v0.8.1/v0.6.x profiles, v1.0.0 introduced the move from `%LOCALAPPDATA%\RvtMcp\` to `%LOCALAPPDATA%\Bimwright\rvt-mcp\`. Installation reports include migration conflicts and locked-folder failures. The guidance below explains recovery; development-build diagnostics are not retroactive fixes to a published package.

| Symptom | What to check and how to recover |
|---|---|
| Installer stops with `Both … exist` | Both legacy and current profile folders exist; the installer refuses to merge them. Back up both while Revit and gateway clients are closed. Inspect which holds the settings and ToolBaker data to retain. With the user's approval, rename the other folder to an unused backup name rather than deleting it, then preview/retry installation. If both contain data to keep, stop and resolve the conflict from backups before retrying; do not discard one blindly. |
| Installer stops with `Could not move …` | An old gateway/client may still lock the legacy folder; permissions may also block the move. Fully quit clients using the gateway, close Revit, and check the error's recovery report and any retained `.rvtmcp-rollback-*` backups. Resolve the lock/permission issue, then preview/retry. Do not purge user data or auto-kill processes. |
| Runtime reports `MIGRATION_REQUIRED` | Legacy/current files conflict or migration is incomplete. Keep source folders and backups, inspect the reported conflict and reconcile the data to retain before retrying. Do not erase the current profile to force fallback settings. |
| A development build reports `PAIRING_REQUIRED`, or target verification says the add-in lacks the required protocol | The gateway and loaded add-in cannot perform verified writes together. Close Revit and clients, update the server and add-ins from one release, update MCPB too if used, then restart both and verify the target/document again. Retrying the same write or adding PID to every call does not repair pairing. |
| `AMBIGUOUS_TARGET` or `TARGET_UNCONFIRMED` without a pairing error | List targets, select the intended instance by its listed PID and verify its open document. Updating packages does not choose a target or confirm a changed document. |
| Client still points at `%LOCALAPPDATA%\RvtMcp\...` or a versioned server folder | Check the active client's command against the install summary's `Server :` path (`%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`). Repoint only that entry, preserve args/env, and restart the target. Custom launchers require approval before changing. See [per-client wiring](mcp-client-wiring.md). |
| Installer succeeded, but the client still has old tools or instructions | Fully restart/reconnect the requested client, inspect fresh serverInfo and tools/list, and compare with `revit_health` where available. Check for a project entry shadowing user config, an old executable in a custom launcher, or a mismatched MCPB. Package verification and client reload are separate stages. |
| Desktop connects twice, or MCPB exposes `rvt_mcp_setup_status` after upgrade | Use [one Desktop registration route](#claude-desktop-choose-one-registration-route). Update Setup and extension from the same selected release; an older extension can reject the new executable's hash and display its own old expected version. Update that extension rather than downgrading a verified newer gateway. `-Client none` neither updates the extension nor removes an old manual entry. |

After recovery, repeat the [three verification stages](#verify-and-hand-off). A closed Revit session or a pending Desktop restart is not a reason to uninstall or purge an otherwise valid installation. If recovery remains blocked, report the old/new versions, target client/install type and exact error at <https://github.com/bimwright/rvt-mcp/issues>; do not attach personal profile data or authentication tokens.

## Uninstall

From the setup ZIP root (or the repo's `scripts/`):

```powershell
# Setup ZIP layout:
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes

# Clone layout:
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -Yes
```

Removes the add-ins for every Revit year 2022–2027 (including Bimwright-era copies), the self-contained server, both legacy `revit-YYYY.json` and per-instance `revit-YYYY-PID.json` discovery files, and the spill cache. Both descriptor formats are removed regardless of the installed version, under the current `%LOCALAPPDATA%\Bimwright\rvt-mcp\` root and any leftover legacy `%LOCALAPPDATA%\RvtMcp\` root. MCP client configs are not touched — use `install.ps1 -Uninstall -Client <names>` to strip just the `rvt-mcp` entry, or remove it from your clients yourself. A server copy that a running MCP client still uses is kept; close the client and run again. Everything else under `%LOCALAPPDATA%\Bimwright\rvt-mcp` (settings, translations, ToolBaker data, firm profiles, shared parameters, logs, captures) is kept. `-Purge` deletes the whole folder; `-Purge -KeepLogs` keeps logs.

## Developer install

```powershell
git clone https://github.com/bimwright/rvt-mcp.git
cd rvt-mcp
dotnet build src/RvtMcp.sln -c Debug
```

Close every Revit first — the build deploys plugin DLLs into `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\`. Point your MCP client at `src/server/bin/Debug/net8.0/RvtMcp.Server.exe`. To exercise the real installer, build a throwaway setup package with `pwsh scripts/package-client-setup.ps1 -AllowDirty` and run `build/client-setup/stage/install.ps1` (`-Years 2024` for one year). Tests, packaging and conventions: [CONTRIBUTING.md](../CONTRIBUTING.md).

## NuGet: server only

This does **not** install Revit plugins. Use it if the add-in is already on the machine (ZIP or a local build) and you want the MCP server on PATH:

```powershell
dotnet tool uninstall -g Bimwright.Rvt.Server   # skip if you never installed the 0.1–0.3 tool
dotnet tool install -g RvtMcp.Server
```

This installs the latest server; it must match your add-in, so add `--version <x.y.z>` if your add-in is older. Command name: `rvt-mcp`. Keep using the GitHub Release ZIP for plugins. `Bimwright.Rvt.Server` is obsolete.

## Migrating from `Bimwright.Rvt.*` (v0.3 and earlier)

v0.4+ renamed packages and folders to `RvtMcp.*` (repo name and brand stay bimwright).

1. Close every Revit.
2. `pwsh scripts/uninstall-old.ps1` — drops old `%APPDATA%\…\Bimwright\` plugins and old server root; keeps user bake/journal data and migrates it to `%LOCALAPPDATA%\Bimwright\rvt-mcp\` on first new launch.
3. Install the current GitHub Release ZIP (see the [README](../README.md#install)). Do not install v0.5.0 or earlier packages. Uninstall the old global tool if present: `dotnet tool uninstall -g Bimwright.Rvt.Server`.
4. Point MCP clients at entry name **`rvt-mcp`** (`install.ps1 -Client` wires it). The installer removes Bimwright-era add-ins automatically; `-Client` reports leftover `bimwright-rvt-r22`… client entries, but removing them stays your call.
