# RvtMcp-MCP

Open-source (Apache-2.0) MCP gateway that lets Claude Code (and any MCP-capable client) drive Autodesk Revit 2022–2027.

## Architecture

Full C# stack. No TypeScript. Single language, single build system. Multi-version: one plugin shell per Revit 2022–2027.

```
MCP client (Claude Code / Cursor / Cline / …) → stdio → C# MCP Server (.NET 8 Console App) → TCP or Named Pipe → per-version C# plugin shell → Revit 2022–2027 API
```

Two processes:
- **RvtMcp.Server.exe** — MCP Server, runs as separate process, stdio transport (ModelContextProtocol NuGet)
- **RvtMcp.Plugin.dll** — Revit addin, loads inside Revit.exe, TCP listener (Revit 2022–2024) or Named Pipe (Revit 2025–2027) + ExternalEvent marshalling. Each Revit version gets its own shell DLL compiled from the same `src/shared/` source glob.

Communication: newline-delimited JSON (NDJSON). Discovery files written per Revit instance in `%LOCALAPPDATA%\Bimwright\rvt-mcp\`:
- `revit-<year>-<pid>.json` — per-instance descriptor (schema 3) for every instance, plus the legacy `revit-<year>.json` for older servers
- `revit-2022.json` / `revit-2023.json` / `revit-2024.json` — legacy per-year files: TCP transport (port OS-assigned) + auth token + PID
- `revit-2025.json` / `revit-2026.json` / `revit-2027.json` — legacy per-year files: Named Pipe transport + auth token + PID

Discovery file format (`schema_version=3`):
```json
{
  "schema_version": 3,
  "host_app": "revit",
  "host_year": 2024,
  "revit_year": 2024,
  "target_id": "revit-2024-67890",
  "pid": 67890,
  "process_start_utc": "2026-10-06T08:00:00.000Z",
  "transport": "tcp",
  "port": 49891,
  "pipe_name": null,
  "auth_token": "...",
  "capabilities": ["tool_catalog", "controlled_failures_v1"]
}
```

`window_title` is not in the descriptor — the server reads it live from the process. The server binds to exactly one instance by scanning these files (skipping any whose `pid` is dead, deduplicating per-instance vs legacy by pid). Use `--target 2022` / `pid:<n>` / `id:revit-<year>-<pid>` (4-digit calendar year, NOT legacy R-codes) to pin a specific instance when multiple Revits run. Agents should call `revit_list_available_targets` to enumerate live instances and `revit_get_current_target` to inspect the pinned target rather than guessing version strings — `revit_switch_target` hard-fails with an educational error if passed an R-code like `R24`.

Other per-user data under the same root:
- `failures\ledger.db` — local failure ledger (operations, incidents, user decisions). Never delete it to clear state; open it read-only when inspecting.
- `catalog\revit-<year>.json` — dialog and failure catalogue the add-in builds at run time from the Revit installed on that machine. It contains Autodesk text: never commit it, package it, or copy its text into tests or docs.

## Project Structure

```
src/
├── RvtMcp.sln         # Solution (Server + 6 plugin shells)
│
├── server/                   # MCP Server (Console App, .NET 8)
│   ├── RvtMcp.Server.csproj
│   ├── Program*.cs           # Host startup, server options/instructions, toolset registration (partial class)
│   ├── ToolGateway*.cs       # SendToRevit / CallAsync + transport client to plugin shells
│   └── Tools/                # One file per toolset class (QueryTools, CreateTools, MetaTools, …)
│
├── shared/                   # Source shared by all plugin shells (glob-included)
│   ├── Handlers/             # Command files grouped by domain; namespaces remain RvtMcp.Plugin.Handlers
│   ├── Commands/             # Ribbon button commands
│   ├── Config/               # Configuration loaders
│   ├── Infrastructure/       # CommandDispatcher, McpEventHandler, ResponseSizeGuard, PendingRequestLifecycle, …
│   ├── Failures/             # Controlled failures: warning/dialog capture, policy, local ledger, Revit message catalogue
│   ├── Transport/            # TcpTransportServer, PipeTransportServer, ITransportServer
│   ├── Logging/              # McpLogger
│   ├── Security/             # AuthToken, SecretMasker
│   ├── Survey/               # Change-impact survey engine
│   ├── Lint/                 # Firm profiles, view-naming analysis
│   ├── Localization/         # UI strings for every locale (completeness is tested)
│   ├── ToolCatalog/          # Tool catalogue models shared with the server
│   ├── Update/               # Update check and notices
│   ├── ToolBaker/            # Self-evolution engine (list_baked_tools, run_baked_tool, adaptive suggestions)
│   └── Views/                # HistoryWindow, History → Issues
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

# Build one shell without deploying (Revit may stay open). In Git Bash use -p:, not /p:.
dotnet build src/plugin-r24/RvtMcp.Plugin.R24.csproj -c Release -p:RvtMcpSkipDeploy=true
```

Each shell auto-deploys to `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` for its target year, plus a matching `RvtMcp.R<nn>.addin` manifest at the parent level.
MCP Server runs from `src/server/bin/Debug/net8.0/RvtMcp.Server.exe`.

## Key Patterns

### Threading
- TCP listener / Named Pipe listener on background thread
- ConcurrentQueue<PendingRequest> with per-request TaskCompletionSource
- ExternalEvent.Raise() marshals to Revit UI thread
- One live command per ExternalEvent (re-Raise if the queue still has work); skip completed TCS (timeout/cancel)
- Wire responses: agent-visible warning from 64 KiB, strong warning above 256 KiB; past the ~700 KiB enforcement budget (compact bytes; headroom below the 1 MiB delivered ceiling) reads reject with command-specific scope hints and completed mutations return compact `success=true` summaries. Eight approved bulk tools support `output=inline|file` to `%LOCALAPPDATA%\Bimwright\rvt-mcp\spill\`; `send_code` and oversized `run_baked_tool` output auto-spill. Spill files are local same-machine artifacts, kept 36h by default (`--spill-retention-hours`, `BIMWRIGHT_SPILL_RETENTION_HOURS`, `spillRetentionHours`; an invalid value uses 36). Age is the only reason to delete one: no file-count cap removes a file younger than the retention time.
- Shutdown: cancel all pending TCS, stop transport, dispose ExternalEvent

### Commands
- IRevitCommand interface: Name, Description, Execute(UIApplication, paramsJson) → CommandResult
- Explicit dictionary registration in CommandDispatcher (not reflection until 10+ commands)
- Each handler = 1 file in `Handlers/<Domain>/`; keep namespace `RvtMcp.Plugin.Handlers`. The solution-level `Shared source` folder displays the same physical source files.
- All return DTOs (anonymous objects) — NEVER serialize Revit API objects directly
- Unit conversion: Revit internal (feet) → mm using SpecTypeId/ForgeTypeId

### MCP Tools
- The bare binary registers the default toolsets (`query,create,view,meta`); `--toolsets all` gives the full surface, plus the adaptive bake tools when enabled. `install.ps1` seeds `rvtmcp.config.json` with `toolsets=all` so installed machines get the full surface. Tool counts are not repeated here: the source of truth is `tests/RvtMcp.Tests/Golden/tools-list*.json`, and the README and CHANGELOG quote them (checked by `ToolCountDocumentationTests`). ALL MCP-facing names prefixed `revit_` (e.g. `revit_create_grid`). Server↔plugin wire command names stay unprefixed and unchanged.
- Tools live in `[McpServerToolType, Toolset("<name>")]` classes grouped by domain (query, create, modify, delete, view, export, annotation, mep, schedule, sheets, materials, geometry, rooms, links, structural, lint, toolbaker, meta), one file each in `src/server/Tools/`. Each tool = `[McpServerTool]` static method with param docs + examples. A shell whose arguments are plain values is `=> await ToolGateway.CallAsync("<wire_command>", new { ... })`; a shell that parses or normalizes input uses `ToolGateway.CallWithParamsAsync("<wire_command>", () => { ...; return new { ... }; })` so the parsing runs inside the helper's `try` and bad input still returns `Error: ...` text. Read-only and timeout guards stay as statements before the call. New classes must also be added to `Program.ResolveRegisteredToolTypes` (`Program.ToolRegistration.cs`).
- Progressive disclosure (A3): default toolsets are `query,create,view,meta`. `--toolsets all` (or an explicit CSV) and `--read-only` gate the rest. `structural` is write-capable and **off** unless requested. `--read-only` filters individual tools by their document/file effects, including read tools within otherwise write-capable toolsets, and removes arbitrary code and baked-tool execution.
- Tool Search (v0.5): server `instructions` field populated at startup (keyword-dense) so MCP clients can discover the surface — it was previously empty, so search returned nothing.
- `revit_batch_execute` (A6): TransactionGroup-wrapped multi-command call, max 20 sub-commands. Caller timeout is separate from native execution: retain the operation ID and query status before retrying; a started request can complete later.

### Controlled failures (warnings and dialogs)
- Warnings, errors and dialogs raised during an MCP call are handled in `src/shared/Failures/`. By default a warning or error rolls the change back, records an incident in the local ledger and returns `_failure` / `_operation` receipts to the agent. The model stays writable unless its state is uncertain. Details: [docs/controlled-failures.md](docs/controlled-failures.md).
- Never add code that dismisses Revit dialogs or deletes warnings just so a call passes. Accepting a warning or answering a dialog needs an explicit, recorded choice (user rules and grants in History → Issues); otherwise it stays with the user, and the agent receives the dialog's information.
- A call that waits on a dialog or outlives its timeout returns a pending receipt with an `operationId` (`waiting_for_user`, `timed_out_running`). The result arrives later through `revit_get_operation_status` (its `waitSeconds` waits on the server) or once as `_previousOperation`. Never resend the request unchanged.
- Dialogs and warnings from the user's own work in Revit stay as Revit shows them; the add-in only notes them and reports them once (`_userDialogs`).
- Non-read commands require a matching gateway and add-in (`controlled_failures_v1`); update both together.

## Revit 2022 Specifics

- .NET Framework 4.8
- ElementId: use `.IntegerValue` (int 32-bit)
- Floor creation: `doc.Create.NewFloor()` (deprecated but only option in Revit 2022)
- Ceiling: `Ceiling.Create()` (available since Revit 2022)
- Parameter types: ForgeTypeId via `GetDataType()` / `SpecTypeId`
- Unit detection: `SpecTypeId.Length`, `SpecTypeId.PipeSize`, etc.
- Newtonsoft.Json 13.0.3 (Revit ships 12.x — binding redirect handles it)
- Roslyn: Microsoft.CodeAnalysis.CSharp 4.8.0, use AppDomain.GetAssemblies() for refs

## Multi-Version Architecture

- MCP Server: one process, NOT affected by Revit version
- Plugin: one thin shell per Revit year (`src/plugin-rXX/`), all compiling the same `src/shared/**` source glob
- Target frameworks: Revit 2022/2023/2024 = .NET 4.8; 2025/2026 = .NET 8 (`net8.0-windows7.0`); 2027 = .NET 10 (`net10.0-windows7.0`)
- Transport: TCP for Revit 2022–2024, Named Pipe for Revit 2025–2027 (`ITransportServer` abstraction in `src/shared/Transport/`)
- API version differences handled via `#if REVIT2024_OR_GREATER` / `REVIT2027_OR_GREATER` and the `RevitCompat` helper in `src/shared/Infrastructure/`
- Revit 2026+: `ElementId.IntegerValue` removed → use `.Value` or `RevitCompat.GetId()`
- Revit 2026+: `EnableDynamicLoading=true` in csproj; Revit 2027 adds `<UseWPF>true</UseWPF>`
- All shells use Nice3point.Revit.Api NuGet packages pinned to the matching year

## Decision Log

- C# MCP Server over TypeScript: single-language simplicity, direct Revit API access patterns
- Single C# project for plugin (not split Plugin/Commands): reduces csproj + build complexity
- Port 0 over port scanning: OS-assigned ports avoid conflicts with other dev tools
- DTO mapping mandatory: Revit objects not serializable (circular refs, COM interop)
- AppDomain.GetAssemblies() for Roslyn refs: fixes Assembly.Load crash in Revit context
- ForgeTypeId/SpecTypeId for unit detection: UnitType deprecated in Revit 2022+
- Named Pipe for Revit 2025+: avoids loopback-firewall prompt UX on modern Windows
