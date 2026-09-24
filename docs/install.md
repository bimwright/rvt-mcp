# Install, upgrade and uninstall

The quick path is in the [README](../README.md#install). This page has the details: what the installer does, upgrades, uninstall, developer and NuGet installs, and migration from `Bimwright.Rvt.*`.

## What the installer does

Client machines should use the setup ZIP from [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest). It bundles a self-contained MCP server and Revit 2022–2027 plugins — no .NET SDK, NuGet global tool, or source clone. AI agents follow [AGENTS.md](../AGENTS.md).

The installer detects Revit 2022–2027 (a year counts when its `Revit.exe` exists), installs the matching add-ins and the server at the fixed path `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe`, checks that the server starts and verifies the add-ins against the package. It does **not** configure MCP clients: register a stdio server named `rvt-mcp` with that command in your client — or let your AI agent do it ([AGENTS.md](../AGENTS.md), Step 3; per-client steps in [mcp-client-wiring.md](mcp-client-wiring.md)).

Do **not** install v0.5.0 or earlier ZIPs. Do **not** `dotnet tool install -g Bimwright.Rvt.Server` (legacy 0.1–0.3). Do **not** use NuGet instead of this ZIP on a Revit client machine — the tool package has no add-in.

Checking a fresh machine: [testing/fresh-install-checklist.md](testing/fresh-install-checklist.md).

## Upgrade

Updates are manual. Close all Revit windows and stop the MCP connection in your AI client, extract the new release ZIP into a separate folder, then run its `install.ps1 -WhatIf` followed by `install.ps1`. Upgrade the server and plugins together; restart Revit and the MCP client, then repeat the [checks in the README](../README.md#check-that-it-works). Do not uninstall first: upgrades replace plugins and the server in place.

The server path never changes between versions, so MCP clients keep working and only need a restart; clients still running the previous copy keep it until they restart (the summary lists it under `In use`, and the next install removes it). Older add-in copies that carry RvtMcp's AddInId — Bimwright-era leftovers — are removed automatically, because Revit would otherwise load only one of them. A machine-wide copy under `%ProgramData%` stops the install (removing it needs admin rights).

Before replacing files, the installer checks package checksums when a manifest is present, validates every selected plugin ZIP (including its add-in manifest), stages the payload and refuses installation while Revit is running. It then starts the new server once (`--help`) and verifies the installed add-ins byte for byte against the package. Any caught error restores the previous add-ins and server. If rollback is blocked by file locks or permissions, the error identifies retained `.rvtmcp-rollback-*` backups; hard termination or power loss requires manual recovery. See [installer verification](testing/installer-upgrade/README.md).

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

Removes the add-ins for every Revit year 2022–2027 (including Bimwright-era copies), the self-contained server, discovery files and the spill cache. MCP client configs are not touched — remove the `rvt-mcp` entry from your clients yourself. A server copy that a running MCP client still uses is kept; close the client and run again. Everything else under `%LOCALAPPDATA%\RvtMcp` (settings, translations, ToolBaker data, firm profiles, shared parameters, logs, captures) is kept. `-Purge` deletes the whole folder; `-Purge -KeepLogs` keeps logs.

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
2. `pwsh scripts/uninstall-old.ps1` — drops old `%APPDATA%\…\Bimwright\` plugins and old server root; keeps user bake/journal data and migrates it to `%LOCALAPPDATA%\RvtMcp\` on first new launch.
3. Install the current GitHub Release ZIP (see the [README](../README.md#install)). Do not install v0.5.0 or earlier packages. Uninstall the old global tool if present: `dotnet tool uninstall -g Bimwright.Rvt.Server`.
4. Point MCP clients at entry name **`rvt-mcp`**. The installer removes Bimwright-era add-ins automatically; remove old per-year `bimwright-rvt-r22`… client entries yourself.
