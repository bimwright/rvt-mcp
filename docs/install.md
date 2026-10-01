# Install, upgrade and uninstall

The quick path is in the [README](../README.md#install). This page has the details: what the installer does, upgrades, uninstall, developer and NuGet installs, and migration from `Bimwright.Rvt.*`.

## What the installer does

Client machines should use the setup ZIP from [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest). It bundles a self-contained MCP server and Revit 2022–2027 plugins — no .NET SDK, NuGet global tool, or source clone. AI agents follow [AGENTS.md](../AGENTS.md).

The installer detects Revit 2022–2027 (a year counts when its `Revit.exe` exists), installs the matching add-ins and the server at the fixed path `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`, checks that the server starts and verifies the add-ins against the package. It also seeds `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json` with `"toolsets": ["all"]` when the file doesn't already set `toolsets` — so a fresh install exposes the full 229-tool surface, while your own `toolsets` choice survives upgrades (a bare `rvt-mcp.exe` without the file still defaults to `query,create,view,meta`). It then wires every MCP client it detects — `-Client <names>` wires only those, `-Client none` leaves client configs untouched, `-WhatIf` previews, and `-Uninstall -Client <names>` removes just the `rvt-mcp` entry. It applies [mcp-client-wiring.md](mcp-client-wiring.md): `.bak` backup before each edit, minimal text edits that keep JSONC comments, repointing of old versioned paths, and reporting (never replacing) of custom launchers and legacy `bimwright-rvt*` entries. Or register a stdio server named `rvt-mcp` by hand — or let your AI agent do it ([AGENTS.md](../AGENTS.md), Step 3).

Do **not** install v0.5.0 or earlier ZIPs. Do **not** `dotnet tool install -g Bimwright.Rvt.Server` (legacy 0.1–0.3). Do **not** use NuGet instead of this ZIP on a Revit client machine — the tool package has no add-in.

On a fresh machine, follow the [README verification steps](../README.md#install) after installing and registering the MCP client.

## Claude Desktop MCPB (v1.0.0 candidate)

This release candidate is not yet published. Use its matching Setup ZIP and
`rvt-mcp-desktop-1.0.0.mcpb` when supplied together. The extension does not install
Revit, the gateway or its add-ins.

1. Close Revit and stop clients using the gateway. Preview the matching installer
   with `install.ps1 -WhatIf -Client none`, then install with `install.ps1 -Client none`.
   To wire other clients, name only those clients with `-Client`; leave Claude Desktop
   to the extension. Existing Desktop config entries are preserved, so remove or
   disable a previous manual `rvt-mcp` registration before enabling the extension.
2. Install the MCPB through Claude Desktop's custom-extension interface. Use one
   registration for this gateway. Start Revit with the matching add-in, then ask
   for `revit_get_current_view_info`.
3. The launcher checks the executable SHA-256 against the server in the matching
   Setup ZIP. Its default path is
   `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe`.
   An override must point to those same executable bytes. Missing or mismatched
   files stop with `rvt_mcp_setup_status`; there is no PATH/global-tool fallback.
   This verifies the server file, not the add-in already loaded by Revit: update
   the gateway and add-ins together and restart Revit.
4. Defaults are all toolsets, full mode, send_code enabled, call logging disabled
   and response guard enabled. The extension also exposes target year and optional
   response-size thresholds. Empty values inherit server configuration; explicit
   send_code, call-log and response-guard choices pass their CLI on/off flags.
   Unchecked read-only sends no override, so read-only set elsewhere still applies.
   Read-only always removes arbitrary code and baked-tool execution.
5. Tools and prompts are discovered on connection. Upgrade the Setup and extension
   together; a new server build needs the matching extension. Removing the
   extension does not uninstall the gateway, add-ins or personal data.

## Upgrade

Older installers used `%LOCALAPPDATA%\RvtMcp\`. The new installer moves that whole folder to `%LOCALAPPDATA%\Bimwright\rvt-mcp\`, flattens `rvt\server\` to `server\`, and preserves settings, ToolBaker data and other personal files. Close Revit and MCP clients using rvt-mcp before this migration. Known client commands are repointed only after the installed server and all selected add-ins pass verification; a caught failure restores the old folder and leaves those commands unchanged. If both product folders already exist, the installer stops without merging them. Use the server and add-ins from the same setup ZIP.

Development runtimes also migrate legacy user data before loading configuration, so
starting a newer server/add-in without the installer does not silently select an empty
profile. They copy settings, ToolBaker data and known user-data folders from `RvtMcp`
or the older shared `Bimwright` root, preserving sources. SQLite uses a consistent
backup including committed WAL content. Partial copies can resume; differing existing
files cause `MIGRATION_REQUIRED` and stop startup before fallback settings are loaded.
Resolve the legacy/current conflict from backups before retrying. Existing current
diagnostic `.log` files are kept, with legacy originals retained at their source.
Completion markers are written only after successful migration.

Runtime security settings combine with the Revit add-in's settings: read-only applies
if either side enables it; `send_code` requires both sides to allow it. Client flags
cannot weaken the policy configured in Revit.

Updates are manual. Close all Revit windows and stop the MCP connection in your AI client, extract the new release ZIP into a separate folder, then run its `install.ps1 -WhatIf` followed by `install.ps1`. Upgrade the server and plugins together; restart Revit and the MCP client, then repeat the [checks in the README](../README.md#install). Do not uninstall first: upgrades replace plugins and the server in place.

The server path never changes between versions, so MCP clients keep working and only need a restart; clients still running the previous copy keep it until they restart (the summary lists it under `In use`, and the next install removes it). Older add-in copies that carry RvtMcp's AddInId — Bimwright-era leftovers — are removed automatically, because Revit would otherwise load only one of them. A machine-wide copy under `%ProgramData%` stops the install (removing it needs admin rights).

Before replacing files, the installer checks package checksums when a manifest is present, validates every selected plugin ZIP (including its add-in manifest), stages the payload and refuses installation while Revit is running. It then starts the new server once (`--help`) and verifies the installed add-ins byte for byte against the package. Any caught error restores the previous add-ins and server. If rollback is blocked by file locks or permissions, the error identifies retained `.rvtmcp-rollback-*` backups; hard termination or power loss requires manual recovery.

Upgrading from v0.6.2 or earlier: those installers put the server in a versioned folder (`...\rvt\server\0.6.2\`). The summary lists such folders under `Legacy`; point your clients at the new `current` path, then remove the old copies with `install.ps1 -PruneOldServers`.

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

Removes the add-ins for every Revit year 2022–2027 (including Bimwright-era copies), the self-contained server, discovery files and the spill cache. MCP client configs are not touched — use `install.ps1 -Uninstall -Client <names>` to strip just the `rvt-mcp` entry, or remove it from your clients yourself. A server copy that a running MCP client still uses is kept; close the client and run again. Everything else under `%LOCALAPPDATA%\Bimwright\rvt-mcp` (settings, translations, ToolBaker data, firm profiles, shared parameters, logs, captures) is kept. `-Purge` deletes the whole folder; `-Purge -KeepLogs` keeps logs.

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
