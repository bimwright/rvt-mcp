# RvtMcp-MCP

Open-source (Apache-2.0) MCP gateway that lets Claude Code (and any MCP-capable client) drive Autodesk Revit 2022–2027.

## Architecture

Full C# stack. No TypeScript. Single language, single build system. Multi-version: one plugin shell per Revit 2022–2027.

```
MCP client (Claude Code / Cursor / Cline / …) → stdio → C# MCP Server (.NET 8 Console App) → TCP or Named Pipe → per-version C# plugin shell → Revit 2022–2027 API
```

Two processes:
- **RvtMcp.Server.exe** — MCP Server, runs as separate process, stdio transport (ModelContextProtocol NuGet)
- **RvtMcp.Plugin.dll** — Revit addin, loads inside Revit.exe, TCP listener (R22–R24) or Named Pipe (R25–R27) + ExternalEvent marshalling. Each Revit version gets its own shell DLL compiled from the same `src/shared/` source glob.

Communication: newline-delimited JSON (NDJSON). Discovery files written per Revit version in `%LOCALAPPDATA%\RvtMcp\`:
- `revit-2022.json` / `revit-2023.json` / `revit-2024.json` — TCP transport (port OS-assigned) + auth token + PID
- `revit-2025.json` / `revit-2026.json` / `revit-2027.json` — Named Pipe transport + auth token + PID

Discovery file format (`schema_version=2`):
```json
{
  "schema_version": 2,
  "revit_year": 2024,
  "transport": "tcp",
  "port": 49891,
  "pipe_name": null,
  "auth_token": "...",
  "pid": 67890
}
```

Server auto-detects which Revit is running by scanning these files (skipping any whose `pid` is dead). Use `--target 2022` etc. (4-digit calendar year, NOT legacy R-codes) to pin a specific version when multiple Revits run. Agents should call `revit_list_available_targets` to enumerate live instances and `revit_get_current_target` to inspect the pinned target rather than guessing version strings — `revit_switch_target` hard-fails with an educational error if passed an R-code like `R24`.

## Project Structure

```
src/
├── RvtMcp.sln         # Solution (Server + 6 plugin shells)
│
├── server/                   # MCP Server (Console App, .NET 8)
│   ├── RvtMcp.Server.csproj
│   └── Program.cs            # MCP tools + transport client to plugin shells
│
├── shared/                   # Source shared by all plugin shells (glob-included)
│   ├── Handlers/             # One file per command (send_code, create_grid, batch_execute, …)
│   ├── Commands/             # Ribbon button commands
│   ├── Config/               # Configuration loaders
│   ├── Infrastructure/       # CommandDispatcher, McpEventHandler, ResponseSizeGuard, …
│   ├── Transport/            # TcpTransportServer, PipeTransportServer, ITransportServer
│   ├── Logging/              # McpLogger
│   ├── Security/             # AuthToken, SecretMasker
│   ├── ToolBaker/            # Self-evolution engine (list_baked_tools, run_baked_tool, adaptive suggestions)
│   └── Views/                # HistoryWindow
│
├── plugin-r22/               # Revit 2022 shell (.NET 4.8, TCP)
├── plugin-r23/               # Revit 2023 shell (.NET 4.8, TCP)
├── plugin-r24/               # Revit 2024 shell (.NET 4.8, TCP)
├── plugin-r25/               # Revit 2025 shell (.NET 8, Named Pipe)
├── plugin-r26/               # Revit 2026 shell (.NET 8, Named Pipe)
└── plugin-r27/               # Revit 2027 shell (.NET 10, Named Pipe)
```

## Build & Deploy

```bash
# Build everything (Server + all 6 plugin shells).
# Revit MUST be closed first — the build auto-deploys plugin DLLs into
# %APPDATA%\Autodesk\Revit\Addins\20XX\RvtMcp\ which Revit would otherwise lock.
dotnet build src/RvtMcp.sln -c Debug

# If RvtMcp.Server.exe is locked by an active MCP client session,
# disconnect the client first — do NOT taskkill it blindly.
```

Each shell auto-deploys to `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` for its target year, plus a matching `RvtMcp.R<nn>.addin` manifest at the parent level.
MCP Server runs from `src/server/bin/Debug/net8.0/RvtMcp.Server.exe`.

## Key Patterns

### Threading
- TCP listener / Named Pipe listener on background thread
- ConcurrentQueue<PendingRequest> with per-request TaskCompletionSource
- ExternalEvent.Raise() marshals to Revit UI thread
- One live command per ExternalEvent (re-Raise if the queue still has work); skip completed TCS (timeout/cancel)
- Wire responses: agent-visible warning from 64 KiB, strong warning above 256 KiB; past the ~700 KiB enforcement budget (compact bytes; headroom below the 1 MiB delivered ceiling) reads reject with command-specific scope hints and completed mutations return compact `success=true` summaries. Eight approved bulk tools support `output=inline|file` to `%LOCALAPPDATA%\RvtMcp\spill\`; `send_code` and oversized `run_baked_tool` output auto-spill. Spill files are local same-machine artifacts, expire after 24h, and are capped at 50.
- Shutdown: cancel all pending TCS, stop transport, dispose ExternalEvent

### Commands
- IRevitCommand interface: Name, Description, Execute(UIApplication, paramsJson) → CommandResult
- Explicit dictionary registration in CommandDispatcher (not reflection until 10+ commands)
- Each handler = 1 file in Handlers/
- All return DTOs (anonymous objects) — NEVER serialize Revit API objects directly
- Unit conversion: Revit internal (feet) → mm using SpecTypeId/ForgeTypeId

### MCP Tools
- 229 Revit tools with `--toolsets all` (232 with adaptive bake). Default surface is **40** tools (`query,create,view,meta`). ALL MCP-facing names prefixed `revit_` (e.g. `revit_create_grid`). Server↔plugin wire command names stay unprefixed and unchanged.
- Tools live in `[McpServerToolType, Toolset("<name>")]` classes grouped by domain (query, create, modify, delete, view, export, annotation, mep, schedule, sheets, materials, geometry, rooms, links, structural, lint, toolbaker, meta). Each tool = `[McpServerTool]` static method with param docs + examples.
- Progressive disclosure (A3): default toolsets are `query,create,view,meta`. `--toolsets all` (or an explicit CSV) and `--read-only` gate the rest. `structural` is write-capable and **off** unless requested. `--read-only` strips every write-capable toolset.
- Tool Search (v0.5): server `instructions` field populated at startup (keyword-dense) so MCP clients can discover the surface — it was previously empty, so search returned nothing.
- `revit_batch_execute` (A6): TransactionGroup-wrapped multi-command call, max 20 sub-commands. `SendToRevit()` helper: transport send + TCS await + 60s timeout (TCS completed on timeout so the UI thread skips the stale request).

## Revit 2022 Specifics

- .NET Framework 4.8
- ElementId: use `.IntegerValue` (int 32-bit)
- Floor creation: `doc.Create.NewFloor()` (deprecated but only option in R22)
- Ceiling: `Ceiling.Create()` (available since R22)
- Parameter types: ForgeTypeId via `GetDataType()` / `SpecTypeId`
- Unit detection: `SpecTypeId.Length`, `SpecTypeId.PipeSize`, etc.
- Newtonsoft.Json 13.0.3 (Revit ships 12.x — binding redirect handles it)
- Roslyn: Microsoft.CodeAnalysis.CSharp 4.8.0, use AppDomain.GetAssemblies() for refs

## Multi-Version Architecture

- MCP Server: one process, NOT affected by Revit version
- Plugin: one thin shell per Revit year (`src/plugin-rXX/`), all compiling the same `src/shared/**` source glob
- Target frameworks: R22/R23/R24 = .NET 4.8; R25/R26 = .NET 8 (`net8.0-windows7.0`); R27 = .NET 10 (`net10.0-windows7.0`)
- Transport: TCP for R22–R24, Named Pipe for R25–R27 (`ITransportServer` abstraction in `src/shared/Transport/`)
- API version differences handled via `#if REVIT2024_OR_GREATER` / `REVIT2027_OR_GREATER` and the `RevitCompat` helper in `src/shared/Infrastructure/`
- Revit 2026+: `ElementId.IntegerValue` removed → use `.Value` or `RevitCompat.GetId()`
- Revit 2026+: `EnableDynamicLoading=true` in csproj; R27 adds `<UseWPF>true</UseWPF>`
- All shells use Nice3point.Revit.Api NuGet packages pinned to the matching year

## Decision Log

- C# MCP Server over TypeScript: single-language simplicity, direct Revit API access patterns
- Single C# project for plugin (not split Plugin/Commands): reduces csproj + build complexity
- Port 0 over port scanning: OS-assigned ports avoid conflicts with other dev tools
- DTO mapping mandatory: Revit objects not serializable (circular refs, COM interop)
- AppDomain.GetAssemblies() for Roslyn refs: fixes Assembly.Load crash in Revit context
- ForgeTypeId/SpecTypeId for unit detection: UnitType deprecated in Revit 2022+
- Named Pipe for R25+: avoids loopback-firewall prompt UX on modern Windows
