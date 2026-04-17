# Architecture

## Two processes, one pipe

```
MCP client (Claude Code / Cursor / …)
        │  stdio (NDJSON)
        ▼
Bimwright.Rvt.Server  (.NET 8 console app, global tool)
        │  TCP (R22–R24)  OR  Named Pipe (R25–R27) — NDJSON + token auth
        ▼
Bimwright.Rvt.Plugin  (Revit add-in DLL, one per year)
        │  ExternalEvent.Raise()
        ▼
Revit API  (UIApplication / Document)
```

**Server** is an MCP server. It talks stdio to the client, translates each tool call into a JSON envelope, and forwards it over a local transport to whichever Revit plugin is running. Server is a plain `.NET tool` — no GUI, no Revit reference.

**Plugin** is an `IExternalApplication` loaded by Revit. It runs a TCP listener (R22–R24) or a Named Pipe server (R25–R27) on a background thread, enqueues requests, and marshals them onto the Revit UI thread via `ExternalEvent.Raise()`. Each request opens its own `Transaction` when it needs one; `batch_execute` wraps many into one `TransactionGroup`.

## Discovery

Every plugin writes a per-version discovery file to `%LOCALAPPDATA%\Bimwright\` on startup:

| Revit | Discovery file | Transport |
|-------|----------------|-----------|
| 2022  | `portR22.txt` | TCP |
| 2023  | `portR23.txt` | TCP |
| 2024  | `portR24.txt` | TCP |
| 2025  | `pipeR25.txt` | Named Pipe |
| 2026  | `pipeR26.txt` | Named Pipe |
| 2027  | `pipeR27.txt` | Named Pipe |

Each file has three lines: transport endpoint, auth token, owning PID. Server scans these on connect, verifies the PID is alive, and auto-deletes orphan files. Explicit `--target R23` pins a specific version when multiple Revits run concurrently.

## Multi-version strategy

Six plugin shells share one source tree. Each csproj glob-includes `src/shared/**/*.cs`:

```
src/
├── server/                # Bimwright.Rvt.Server (.NET 8)
├── shared/                # Handlers, CommandDispatcher, Transport, Logging, Security, ToolBaker
│   ├── Handlers/          # 28 tool handlers
│   ├── Infrastructure/    # CommandDispatcher, McpEventHandler, SchemaValidator, BatchExecutor
│   ├── Transport/         # ITransportServer, TcpTransportServer, PipeTransportServer
│   ├── Security/          # AuthToken, ErrorSanitizer, SecretMasker
│   ├── ToolBaker/         # Roslyn self-evolution engine
│   ├── Config/            # BimwrightConfig — 3-layer precedence
│   └── Views/             # HistoryWindow
├── plugin-r22/            # net48, TCP
├── plugin-r23/            # net48, TCP
├── plugin-r24/            # net48, TCP
├── plugin-r25/            # net8.0-windows7.0, Named Pipe
├── plugin-r26/            # net8.0-windows7.0, Named Pipe
└── plugin-r27/            # net10.0-windows7.0, Named Pipe
```

Year-specific API differences live behind `#if REVIT2024_OR_GREATER` / `#if REVIT2027_OR_GREATER` and the `RevitCompat` helper (e.g. `ElementId.IntegerValue` vs `.Value` across the R2026 cut). Each shell defines its own `DefineConstants` in csproj.

The **server** is version-agnostic — it just forwards JSON envelopes. All Revit-API coupling lives in the plugin.

## Request lifecycle

1. MCP client sends `tools/call` over stdio.
2. `ToolGateway` (server) picks the right plugin connection by `--target` or auto-detect.
3. Server writes an NDJSON envelope `{ id, command, params }` over TCP/Pipe.
4. Plugin's listener thread enqueues the request and raises the ExternalEvent.
5. Revit invokes `McpEventHandler.Execute()` on the UI thread:
   - Schema validation (reject malformed early with `error / suggestion / hint`).
   - Dispatch to the handler via `CommandDispatcher`.
   - Handler opens its own `Transaction` if it mutates state.
   - Sanitize any leaking paths in error messages.
6. Response envelope travels back up the same pipe.
7. `ToolGateway` resolves the `TaskCompletionSource`; MCP tool method returns the DTO.

Timeout: 30s per request on the server side. Listener cancels pending requests on plugin shutdown.

## Progressive disclosure (A3)

Tools are grouped into 10 toolset classes (`QueryTools`, `CreateTools`, `ViewTools`, …). `Program.RegisterToolsets` only calls `.WithTools<T>()` for enabled toolsets, so disabled tools never appear in `tools/list` — the model can't accidentally call what it can't see. `ToolsetFilter.Resolve` handles defaults (`query+create+view+meta`), the `"all"` shortcut, unknown-token tolerance, and `--read-only` post-processing (strips `create`/`modify`/`delete` after expansion).

## Batch execution (A6)

`batch_execute` is the one MCP tool that forwards a list. Plugin-side, `BatchExecuteHandler` opens a `TransactionGroup` and dispatches each sub-command through the normal handler path (so each handler gets its own inner `Transaction` — Revit forbids nested transactions, but a group around them is allowed). On success it calls `Assimilate()` to merge everything into one undo step. On failure it calls `RollBack()` unless `continueOnError=true`. Iteration logic is factored into `BatchExecutor.Run` so it's unit-testable without a live Revit document.

## ToolBaker pipeline

```
bake_tool(name, code) ─┐
                       ▼
          Roslyn CSharpCompilation
                       │  (refs: AppDomain.GetAssemblies() — avoids Assembly.Load crash in Revit)
                       ▼
           In-memory Assembly
                       ▼
     AssemblyLoadContext (isolated per tool)
                       ▼
          Register as IRevitCommand
                       │
run_baked_tool(name) ──┘
```

Baked tools persist across Revit restarts via SQLite. `send_code_to_revit` is the unsandboxed cousin — it executes arbitrary C# against the running Revit session. Gated behind `ALLOW_SEND_CODE` which is Debug-only.

## Config precedence (A9)

```
BimwrightConfig.Load(args, configFilePath)
   │
   ├── Layer 1: JSON file at %LOCALAPPDATA%\Bimwright\bimwright.config.json
   ├── Layer 2: env vars (BIMWRIGHT_TARGET, BIMWRIGHT_TOOLSETS, …)
   └── Layer 3: CLI args (--target, --toolsets, --read-only, …)
                    (later layers overwrite earlier; nulls don't override)
```

Hand-rolled parser, no `System.CommandLine` dep. Each field is nullable so "not set" is distinguishable from "set to default".

## Threading

- Transport listener: background thread, never touches Revit API.
- Request queue: `ConcurrentQueue<PendingRequest>` + per-request `TaskCompletionSource`.
- ExternalEvent: Revit calls back on the UI thread; handler drains the queue there.
- Try/catch per command in the queue drain (no starvation).
- Stale-command guard: skip if the TCS is already completed (timeout/cancel).
- Shutdown: cancel all pending TCS, stop socket/pipe, dispose ExternalEvent, delete own discovery file.

## Why this shape

- **Full C#, one stack.** No TypeScript bridge, no Python helper, no IPC format hop. One language across server, plugin, handlers, ToolBaker, tests.
- **Process split, not thread split.** The server lives outside Revit so it can start before Revit and outlive a plugin crash. Users update the server via `dotnet tool update -g` without touching their add-in.
- **Source glob, not shared DLL.** Each plugin shell compiles `src/shared/**` directly so version-specific `#if` branches produce distinct binaries — no runtime version sniffing, no reflection fallback.
