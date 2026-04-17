<!-- mcp-name: io.github.bimwright/rvt-mcp -->

<p align="center">
  <img src="docs/images/bimwright-logo.jpg" alt="Bimwright — forging the digital craft of the built environment" width="420" />
</p>

<p align="center">
  <a href="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml"><img src="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/.NET-4.8%20%7C%208%20%7C%2010-512BD4" alt=".NET" /></a>
</p>

<p align="center">
  📖 <a href="README.vi.md">Đọc bằng tiếng Việt</a>
</p>

**Bimwright — the predictable Revit MCP server.**

Pure C#. Apache-2.0. **28 tools across Revit 2022–2027**, transaction-safe and auditable. Extensible via ToolBaker when you need more.

Built for AI agents and BIM workflows that want **reversible, reviewable edits** — not black-box autopilot.

Key traits:

- **Full R22–R27 span.** One codebase, six plugin shells, .NET 4.8 → .NET 10. Most peers skip at least one year.
- **Pure C# + Apache-2.0.** No Node.js runtime beside Revit. Enterprise-safe license + audit-ready dependency graph.
- **Transaction-safe batching.** `batch_execute` wraps a command list in one Revit `TransactionGroup` — one undo, atomic rollback on failure.
- **Progressive disclosure.** `--toolsets` + `--read-only` gate what the model sees. Weak models stay sharp.
- **ToolBaker self-extension.** The model writes, compiles, and registers new Revit tools at runtime (Debug).

---

## Architecture

```
MCP client (Claude Code, etc.) ⇄ stdio ⇄ Bimwright.Rvt.Server (.NET 8) ⇄ TCP/Pipe ⇄ Bimwright.Rvt.Plugin.R<nn> (inside Revit.exe) ⇄ Revit API
```

Two processes. The **server** is a .NET global tool; the **plugin** is a per-Revit-year add-in DLL. See [ARCHITECTURE.md](ARCHITECTURE.md) for the full picture.

---

## Install

### 1. Server — .NET tool

```bash
dotnet tool install -g Bimwright.Rvt.Server
bimwright-rvt --help
```

Requires .NET 8 SDK on the machine that runs the MCP client.

### 2. Plugin — Revit add-in

Download the latest release from [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest). Extract it and run:

```powershell
pwsh install.ps1            # detects every installed Revit year
pwsh install.ps1 -WhatIf    # preview without changes
pwsh install.ps1 -Uninstall # clean removal
```

The script detects installed Revit versions via `HKLM:\SOFTWARE\Autodesk\Revit\` and copies the matching plugin into `%APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright\`.

### 3. Wire up your MCP client

Add one entry per Revit year to your client's MCP config (e.g. `.mcp.json`):

```json
{
  "mcpServers": {
    "bimwright-rvt-r23": {
      "command": "bimwright-rvt",
      "args": ["--target", "R23"]
    }
  }
}
```

Drop the `--target` flag and Bimwright auto-detects the running Revit instance via discovery files in `%LOCALAPPDATA%\Bimwright\`.

---

## Supported MCP clients

| Client | Status | Notes |
|--------|--------|-------|
| Claude Code CLI | ✅ verified | primary test target |
| Claude Desktop | ✅ verified | `.mcp.json` entry |
| Cursor | ⏳ pending verification | stdio; expected to work |
| Cline (VS Code) | ⏳ pending verification | stdio; expected to work |
| Other MCP clients | ⏳ pending | open an issue if you try one |

Broader client-compat matrix is on the v0.2 roadmap.

---

<!-- TODO v0.2: add demo.gif above this section -->
## Quickstart — 5 minutes to first tool call

1. `dotnet tool install -g Bimwright.Rvt.Server` + `pwsh install.ps1`.
2. Open Revit, click the **Bimwright → Start MCP** ribbon button.
3. In your MCP client, run `tools/list` — you should see the default toolsets (`query`, `create`, `view`, `meta`).
4. Call `get_current_view_info` — you'll get back a DTO like:
   ```json
   { "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
   ```
5. Try something real:
   ```
   batch_execute({
     "commands": "[
       {\"command\":\"create_grid\",\"params\":{\"name\":\"A\",\"start\":[0,0],\"end\":[20000,0]}},
       {\"command\":\"create_level\",\"params\":{\"name\":\"L2\",\"elevation\":3000}}
     ]"
   })
   ```
   One undo step, both ops committed atomically.

---

## Toolsets

**28 tools across 10 groups.** Four groups are on by default (`query`, `create`, `view`, `meta`); the rest opt in via `--toolsets` or config.

| Toolset | Tools | Default |
|---------|-------|---------|
| `query` | get current view, selected elements, available family types, material quantities, model stats, AI element filter | **on** |
| `create` | grid, level, room, line-based, point-based, surface-based element | **on** |
| `view` | create view, get current view info, place view on sheet | **on** |
| `meta` | `show_message`, `batch_execute` | **on** |
| `modify` | `operate_element`, `color_elements` | off |
| `delete` | `delete_element` | off |
| `annotation` | `tag_all_rooms`, `tag_all_walls` | off |
| `export` | `export_room_data` | off |
| `mep` | `detect_system_elements` | off |
| `toolbaker` | `bake_tool`, `list_baked_tools`, `run_baked_tool`, `send_code_to_revit` *(Debug only)* | off |

Enable with `--toolsets query,create,modify,meta` or `--toolsets all`. Add `--read-only` to strip `create`/`modify`/`delete` regardless of what you requested.

---

## Supported Revit versions

| Revit | Target Framework | Transport | Notes |
|-------|------------------|-----------|-------|
| 2022  | .NET 4.8 | TCP | |
| 2023  | .NET 4.8 | TCP | |
| 2024  | .NET 4.8 | TCP | |
| 2025  | .NET 8 (`net8.0-windows7.0`) | Named Pipe | First .NET 8 shell |
| 2026  | .NET 8 (`net8.0-windows7.0`) | Named Pipe | `ElementId.IntegerValue` removed — uses `RevitCompat.GetId()` |
| 2027  | .NET 10 (`net10.0-windows7.0`) | Named Pipe | Experimental — .NET 10 still preview |

Compile gate is 6/6; runtime verified 4/4 on R23–R26 (see `A1` in the commit history). R22 and R27 ship on compile-evidence because the stack is identical to R23 and R26 respectively.

---

## Security

- **Default loopback bind.** TCP transport listens on `127.0.0.1` only. Opt in to `0.0.0.0` with `BIMWRIGHT_ALLOW_LAN_BIND=1`.
- **Token-gated handshake.** Every connection must present a per-session token written only to `%LOCALAPPDATA%\Bimwright\portR<nn>.txt`.
- **Strict schema validation.** Malformed tool calls are rejected with an error-as-teacher envelope (`error`, `suggestion`, `hint`) before any handler runs.
- **Path-leak mask.** Handler exceptions are sanitized before they reach the MCP response or logs — no absolute paths, UNC shares, or user-home dirs leak out.

See [the security appendix](docs/roadmap.md#security) for the full threat model.

---

## Configuration

Three layers, later wins: **JSON file → env vars → CLI args**.

| Setting | CLI | Env | JSON key |
|---------|-----|-----|----------|
| Target Revit year | `--target R23` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| Allow LAN bind | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| Enable ToolBaker | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |

JSON file path: `%LOCALAPPDATA%\Bimwright\bimwright.config.json`.

---

## ToolBaker — Cook your own tool with your true dataflow

Generic MCP tools force one-size-fits-all workflows. Real BIM tasks aren't — when the model stitches 10+ primitive tools together to do your *actual* job, you pay in time and tokens every single session. That was the itch. ToolBaker is the scratch.

You describe the workflow once. The model writes C# against the Revit API, `bake_tool` compiles it through Roslyn into an isolated `AssemblyLoadContext`, and registers it as a first-class MCP tool. Next session, your workflow is a **single call** — no re-planning, no re-invented glue code, no token burn.

**How it works:**

1. Describe your real dataflow in plain language (e.g. *"schedule every door by fire rating, tag failures, export to CSV"*).
2. The model generates a handler matching the `IRevitCommand` contract.
3. `bake_tool` compiles via Roslyn, links against the live Revit API, loads into a sandboxed assembly context.
4. SQLite persists the bake. Auto-registered on every future Bimwright session.
5. Call it like any built-in — same schema validation, same transaction safety.

Gated behind `--enable-toolbaker` (off by default). `send_code_to_revit` — the unsandboxed escape hatch — is Debug-build only.

---

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) — process model, transport, multi-version strategy, ToolBaker pipeline.
- [CONTRIBUTING.md](CONTRIBUTING.md) — dev setup, build matrix, coding style.
- [docs/roadmap.md](docs/roadmap.md) — v0.2 (MCP Resources, ToolBaker hardening), v0.3 (async job polling, aggregator listings), v1.0 (governance).

---

## License

Apache-2.0. See [LICENSE](LICENSE).
