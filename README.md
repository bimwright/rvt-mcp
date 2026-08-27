<!-- mcp-name: io.github.bimwright/rvt-mcp -->

<p align="center">
  <img src="https://raw.githubusercontent.com/bimwright/.github/master/assets/logos/rvt-mcp.png" alt="rvt-mcp" width="180" />
</p>

<h1 align="center">rvt-mcp</h1>

<p align="center">
  MCP gateway for Autodesk Revit — local tools for agents, optional personal bake loop
</p>

<p align="center">
  <a href="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml"><img src="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/Revit-2022--2027-186BFF" alt="Revit 2022-2027" /></a>
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-229%20tools-6C47FF" alt="MCP tools" /></a>
</p>

<p align="center">
  English · <a href="README.vi.md">Tiếng Việt</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>
</p>

---

## Install

**No setup ZIP is published.** GitHub Releases were unpublished so v0.5.0 and earlier are not offered as a download. The v0.6.0 client ZIP is not out yet.

Do **not** fetch old tags, old `RvtMcp.Setup-*.zip` URLs, or `dotnet tool install -g Bimwright.Rvt.Server` / `RvtMcp.Server`.

**If you are an AI agent:** stop. Tell the user there is no published installer. Do not clone, build, or install from NuGet unless they explicitly asked for a developer setup. Protocol: [AGENTS.md](AGENTS.md).

Until a new GitHub Release exists, the only supported install is from source (Developer install below).

For AutoCAD, use [dwg-mcp](https://github.com/bimwright/dwg-mcp) separately — different product, different install.

### Check that it works

1. Open Revit with a model.
2. Start the MCP connection from the ribbon (BIMwright / RvtMcp panel).
3. From the MCP client, list tools, then call `revit_get_current_view_info`.

You should get something like:

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

If that fails, install is not done yet — fix client config / plugin load before anything else.

### Uninstall

From the setup ZIP root (or this repo’s `scripts/`):

```powershell
# Setup ZIP layout:
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes

# Clone layout:
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -Yes
```

Removes plugins, self-contained server, client entries, discovery files, logs, and ToolBaker cache.

### Developer install

```powershell
git clone https://github.com/bimwright/rvt-mcp.git
cd rvt-mcp
dotnet build src/RvtMcp.sln -c Debug
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -SourceDir . -Client none
```

Close every Revit first — the build deploys plugin DLLs into `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\`. `install.ps1` finds Revit 2022–2027, copies the server under `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\`, and wires detected MCP clients (`-Client codex|opencode|claude|kilo|none`, `-Years 2024` for one year).

Do not `dotnet tool install` `RvtMcp.Server` or `Bimwright.Rvt.Server` — those packages are not a current install path.

### Migrating from `Bimwright.Rvt.*` (v0.3 and earlier)

v0.4+ renamed packages and folders to `RvtMcp.*` (repo name and brand stay bimwright).

1. Close every Revit.
2. `pwsh scripts/uninstall-old.ps1` — drops old `%APPDATA%\…\Bimwright\` plugins and old server root; keeps user bake/journal data and migrates it to `%LOCALAPPDATA%\RvtMcp\` on first new launch.
3. Install from source (Developer install above). Wait for a v0.6.0 GitHub Release ZIP rather than installing any older published package.
4. Point MCP clients at entry name **`rvt-mcp`** (old per-year `bimwright-rvt-r22`… entries are removed by the installer).

---

## What this is

`rvt-mcp` is a **local** bridge between an MCP client (Claude, Cursor, Codex, OpenCode, …) and a running Revit session.

Two processes:

| Piece | Role |
|--------|------|
| **RvtMcp.Server** | .NET 8 MCP server on stdio. No Revit reference — builds anywhere. |
| **RvtMcp.Plugin** | One thin add-in per Revit year (2022–2027). Runs inside Revit, executes on the UI thread. |

Agent → MCP → server → localhost TCP (≤2024) or Named Pipe (≥2025) → plugin → Revit API.

Everything stays on the machine. No cloud relay is required for the gateway itself.

There is no Node/TypeScript sidecar. Server, plugins, handlers, and ToolBaker are C# end to end. Shared command code lives in `src/shared/`; each year is a small shell project with `#if` where the API drifted. Details: [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Why it exists

People who live in Revit already know what they want automated. The friction was always shipping that idea as software: learn enough C#/Dynamo, fight the API, package an add-in, survive version upgrades — or pay someone else, or buy a fixed tool that only half-matches the office.

Agents change the first half of that loop (describe the task, try it live). They do not remove transactions, units, selection, worksharing, or “did this just trash the model?” That is what this gateway is for: a **typed tool surface** for common work, plus an escape hatch when you need ad-hoc C# inside Revit, plus an **optional** path to turn repeated local patterns into personal tools (ToolBaker).

It is not a universal add-in for every firm. Offices differ. The bet is: start from a shared runtime, grow *your* tools on top.

**Scope posture (honest):** we do not mint a new MCP tool for every edge case. Prefer typed tools when they exist; for everything else use `revit_send_code_to_revit` (C# only). Family *project* management is covered; full Family Editor authoring suites and Revit Viewer hosts are out of scope for now — see [docs/roadmap.md](docs/roadmap.md).

---

## How a normal session looks

1. Revit open with a model; plugin connected (ribbon).
2. MCP client starts `rvt-mcp` / the installed server.
3. Agent uses tools: query view/selection, create grids/rooms, sheets, MEP, export, … Lengths in **mm** at the tool boundary.
4. Several writes in one undo step: `revit_batch_execute`.
5. Multiple Revits running: `revit_list_available_targets` then `revit_switch_target` with a four-digit year (`2024`, not `R24`).

When no typed tool fits:

```text
revit_send_code_to_revit   # C# body, compiled and run inside the plugin
```

That tool is on by default (toolset `meta`). Strip it with `--read-only` or `--disable-toolbaker` if you do not want agents compiling code in the model.

### ToolBaker (optional)

Default surface includes `revit_send_code_to_revit`. `revit_list_baked_tools` / `revit_run_baked_tool` need `--toolsets toolbaker` (or `--toolsets all`).

**Adaptive bake** (suggest new tools from usage) is **off** unless you enable it. When on, repeated patterns can show up under `revit_list_bake_suggestions`; you accept or dismiss explicitly. Nothing ships itself into your ribbon without that step.

Useful flags (also JSON / env — see [Configuration](#configuration)):

| Goal | What to turn on |
|------|------------------|
| Learn tools from repeated **typed** calls | `--enable-adaptive-bake` |
| Also cluster **`send_code`** bodies for suggestions | plus `--cache-send-code-bodies` (redacted; still local) |
| Short-lived disk journal of send_code bodies | `persistSendCodeBodies` + TTL (default privacy keeps this off) |

Bake compile runs **inside Revit** via Roslyn — end users do not need Visual Studio. Details and privacy notes: [docs/bake.md](docs/bake.md).

### Toast (optional)

Completion toast in Revit is **off** by default. Turn on with the ribbon **Toast** button, `enableToast` in config, or `BIMWRIGHT_ENABLE_TOAST=1`. Only finished calls are shown (no “in progress” toast). Capture success can show a thumbnail when the file sits under the path allowlist. Ribbon **Status** also prints toast + bake/privacy flags so you can see what is enabled without guessing.

---

## Architecture (short)

```text
MCP client (stdio)
    → RvtMcp.Server (.NET 8)
        → TCP (Revit 2022–2024) or Named Pipe (2025–2027)
            → Plugin shell (per year)
                → ExternalEvent → Revit API / transactions / undo
```

Handlers return plain DTOs — never live Revit objects on the wire.

---

## Tools

Counts (without counting personal baked tools):

| Mode | Tools | Notes |
|------|------:|-------|
| Default | **40** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **229** | Full catalog |
| `all` + adaptive bake | **232** | Adds 3 suggestion-lifecycle tools |

Tool names are MCP-facing as `revit_*`. Wire names between server and plugin stay unprefixed snake_case.

**Default-on toolsets:** `query`, `create`, `view`, `meta`

**Off unless you ask:** everything else (`export`, `geometry`, `mep`, `structural`, `toolbaker`, `modify`, `delete`, …).  
Example: `--toolsets query,view,meta` or `--toolsets all`.  
`--read-only` drops every write-capable toolset (including `create`).

| Toolset | What it covers | Default |
|---------|----------------|---------|
| `query` | View, selection, filters, stats, parameters, relationships, worksets, groups/assemblies | on |
| `create` | Grids, levels, rooms, line/point/surface-based elements, groups | on |
| `view` | Create views, sheets layout helpers, capture image, crop/scale | on |
| `meta` | Batch execute (max 20), multi-Revit targets, project info, purge unused (MVP), message, send_code | on |
| `lint` | View naming patterns, firm-profile detect, warnings summary | off |
| `schedule` | List/create schedules, fields, formulas, data | off |
| `families` | Load/unload, types, instances, audit, export `.rfa` (project-side) | off |
| `modify` | Operate/color elements, set parameters, change type, workset assign | off |
| `delete` | Delete by id | off |
| `annotation` | Tags, text, dimensions, regions, keynotes, checks | off |
| `export` | PDF/DWG/IFC/NWC helpers, room data, and related export tools | off |
| `mep` | Systems, connectors, networks, place terminals/fixtures, etc. | off |
| `graphics` | View filters, overrides, visibility/phase | off |
| `toolbaker` | list/run baked tools; suggestion tools only if adaptive on | off |
| `sheets` | Sheets, titleblocks, revisions, renumber | off |
| `materials` | Materials, appearance, assignment, takeoff | off |
| `geometry` | BBox, measure, clash, volume/area, … | off |
| `rooms` | Rooms/areas/spaces, finishes, separators | off |
| `links` | Revit/CAD links, coordinate audit, acquire/publish coordinates | off |
| `parameters` | Project/shared parameters | off |
| `organization` | Saved selections, view templates | off |
| `workflows` | Composite clash/audit/sheet/takeoff-style flows | off |
| `structural` | Columns, beams, foundations, rebar, loads, … | off |
| `kei` | Active KEI project DB path, query/write SQLite (WAL-safe), equipment import | off |

### Representative tools

Not a full dump of 200+ schemas — just anchors agents and humans use often:

| Toolset | Tool | Role |
|---------|------|------|
| `query` | `revit_get_current_view_info` | Active view type, level, scale |
| `query` | `revit_get_selected_elements` | Current selection |
| `query` | `revit_ai_element_filter` | Category + parameter filter (mm) |
| `query` | `revit_get_element_details` | Location, bbox, workset, phase, … |
| `create` | `revit_create_grid` / `revit_create_level` / `revit_create_room` | Core layout |
| `create` | `revit_create_point_based_element` | Doors, furniture, … from type id |
| `view` | `revit_capture_view_image` | Raster capture (path allowlist) |
| `meta` | `revit_batch_execute` | One `TransactionGroup` for several commands |
| `meta` | `revit_list_available_targets` / `revit_switch_target` | Multi-Revit |
| `families` | `revit_load_family_from_path` | Load `.rfa` into the project |
| `links` | `revit_get_project_coordinate_system` / `revit_get_link_coordinate_system` | Host/link origins, sites, transforms, True North |
| `toolbaker` | `revit_send_code_to_revit` | Escape hatch (C#) |
| `toolbaker` | `revit_list_baked_tools` / `revit_run_baked_tool` | Personal accepted tools |
| `toolbaker` | `revit_list_bake_suggestions` | Adaptive only |
| `lint` | `revit_analyze_view_naming_patterns` | Naming outliers |

Golden snapshots in tests pin the exact surface; if counts and code disagree, trust tests/code.

---

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Compile matrix covers all six shells. Runtime depth still varies by year — bake and custom C# should be rechecked on the years you care about.

**Host:** full Revit desktop only. Revit Viewer is not a supported target.

---

## Security and privacy

- Transport is local by default (loopback TCP / local named pipe).
- Discovery files under `%LOCALAPPDATA%\RvtMcp\` include a per-session auth token.
- Tool arguments are schema-checked before handlers run.
- Errors returned to the model are sanitized (path leakage reduced).
- `send_code` can run arbitrary C# in the Revit process — powerful and risky; disable toolbaker if that is unacceptable.
- Adaptive bake, body cache, and TTL journals are **opt-in** and stay under the user profile. Defaults do not write raw send_code bodies to long-lived logs.

More: [SECURITY.md](SECURITY.md), [docs/bake.md](docs/bake.md).

---

## Configuration

Precedence, high wins: **CLI → env (`BIMWRIGHT_*`) →** `%LOCALAPPDATA%\RvtMcp\rvtmcp.config.json`.

| Setting | CLI | Env | JSON |
|---------|-----|-----|------|
| Target year | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| LAN bind (plugin) | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker surface | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache send_code bodies (bake clusters) | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| Persist send_code journal | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| Completion toast | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=1` | `enableToast` |

After changing server flags, restart the MCP connection so the client picks up the new tool list.

---

## MCP clients

| Client | Wiring |
|--------|--------|
| Claude Code | project `.mcp.json` or `~/.claude.json` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenCode / Codex / Kilo | `install.ps1 -Client …` (scripted) |
| Cursor / Cline / VS Code Copilot | documented JSON layouts |
| Gemini CLI / Antigravity | `gemini mcp add` or settings JSON |

Installer auto-detect is usually enough; see [AGENTS.md](AGENTS.md) and `docs/mcp-config-*.md` when hand-editing.

---

## Repo layout

```text
rvt-mcp/
├── src/
│   ├── RvtMcp.sln
│   ├── server/            # MCP server
│   ├── shared/            # Handlers, transport, ToolBaker, toast, …
│   ├── plugin-r22/ … r27/ # One shell per Revit year
├── tests/                 # xUnit + golden tool lists
├── scripts/               # install / uninstall / package
├── docs/                  # roadmap, bake, testing
├── AGENTS.md
└── ARCHITECTURE.md
```

---

## Development

```bash
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj
dotnet build src/server/RvtMcp.Server.csproj -c Release
dotnet build src/plugin-r26/RvtMcp.Plugin.R26.csproj -c Release
```

Close Revit before building plugins (DLL lock). Plugin projects deploy into `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` on a normal Debug/Release build.

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

Contribution norms and snapshot rules: [CONTRIBUTING.md](CONTRIBUTING.md).

### Maturity

Usable, not sacred. CI builds the six plugin shells and server tests. Runtime coverage is strongest on mid-range years; treat production models carefully and verify on *your* Revit build. Fresh-machine checklist: [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md).

---

## More docs

| Doc | Topic |
|-----|--------|
| [AGENTS.md](AGENTS.md) | Agent install protocol |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Processes, transport, DTO rules |
| [docs/bake.md](docs/bake.md) | Adaptive bake and body privacy |
| [docs/roadmap.md](docs/roadmap.md) | Near-term hardening and non-goals |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | KEI SQLite tools (`--toolsets kei`) |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

---

## bimwright

Same house style across AEC hosts:

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — Vietnamese-first BIM notes  

---

## License

Apache-2.0 — [LICENSE](LICENSE).

Revit and Autodesk are trademarks of Autodesk, Inc. bimwright is independent and not affiliated with Autodesk.
