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
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  English · <a href="README.vi.md">Tiếng Việt</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>
</p>

---

## What it is

`rvt-mcp` is a **local** bridge between an MCP client and a running Revit session. A .NET 8 server talks MCP over stdio; a thin add-in per Revit year (2022–2027) runs inside Revit and is reached over localhost TCP (≤2024) or a named pipe (≥2025). Nothing leaves the machine, it is C# end to end, and lengths are millimetres at the tool boundary. Details: [ARCHITECTURE.md](ARCHITECTURE.md).

Agents get a **typed tool surface** for common Revit work, a C# escape hatch for everything else, and an **optional** way to turn repeated patterns into personal tools (ToolBaker): start from a shared runtime and grow *your* tools on top. Family Editor authoring is out of scope for now ([roadmap](docs/roadmap.md)).

---

## Install

Use the setup ZIP from [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest): a self-contained server plus the Revit 2022–2027 add-ins, no .NET SDK or source clone needed. **AI agents:** follow [AGENTS.md](AGENTS.md) and do not clone or build unless the user asked for a developer setup.

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

Close Revit first. The installer finds each Revit 2022–2027 that has a `Revit.exe`, installs the matching add-ins and the server at `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe`, verifies both and rolls back on error. It does not touch MCP client configs. Details and other install paths (developer, NuGet server only): [docs/install.md](docs/install.md).

### Connect your MCP client

Register one stdio server named `rvt-mcp` whose command is the server path above, written out as an absolute path, using the client's own `mcp add` command, settings UI or config file. Any stdio MCP client works (Claude Code, Claude Desktop, Codex, Cursor, VS Code, Gemini CLI, OpenCode, Kilo, …). Verified per-client steps: [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md).

### Check that it works

1. Open Revit with a model.
2. Start the MCP connection from the ribbon (**Add-Ins** tab → **RvtMcp** panel).
3. From the MCP client, list tools, then call `revit_get_current_view_info`.

You should get something like:

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

If that fails, the install is not done yet — fix the client config or add-in load first.

### Upgrade

Close Revit and the MCP client, extract the new release ZIP to a new folder, then run its `install.ps1 -WhatIf` followed by `install.ps1` — do not uninstall first. The server path stays the same, so clients only need a restart. Coming from v0.6.2 or earlier? Point your clients at the `current` path above, then remove old server folders with `install.ps1 -PruneOldServers`. More: [docs/install.md](docs/install.md#upgrade).

### Uninstall

From the setup ZIP folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes
```

Removes the add-ins and the server but keeps settings, translations, ToolBaker data and logs unless you add `-Purge`. Remove the `rvt-mcp` entry from your MCP clients yourself. More: [docs/install.md](docs/install.md#uninstall).

---

## Tools

| Mode | Tools | Notes |
|------|------:|-------|
| Default | **40** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **229** | Full catalog |
| `all` + adaptive bake | **232** | Adds 3 suggestion-lifecycle tools |

Counts exclude your personal baked tools. Other toolsets stay off until you ask for them, e.g. `--toolsets query,view,meta,mep` or `--toolsets all`; `--read-only` drops every write-capable toolset (including `create`).

| Toolset | What it covers |
|---------|----------------|
| `query` | View, selection, filters, stats, parameters, relationships, worksets, groups/assemblies |
| `create` | Grids, levels, rooms, line/point/surface-based elements, groups |
| `view` | Create views, sheets layout helpers, capture image, crop/scale |
| `meta` | Batch execute (max 20), multi-Revit targets, project info, purge unused (MVP), message, send_code |
| `lint` | View naming patterns, firm-profile detect, warnings summary |
| `schedule` | List/create schedules, fields, formulas, data |
| `families` | Load/unload, types, instances, audit, export `.rfa` (project-side) |
| `modify` | Operate/color elements, set parameters, change type, workset assign |
| `delete` | Delete by id |
| `annotation` | Tags, text, dimensions, regions, keynotes, checks |
| `export` | PDF/DWG/IFC/NWC helpers, room data, and related export tools |
| `mep` | Systems, connectors, networks, place terminals/fixtures, etc. |
| `graphics` | View filters, overrides, visibility/phase |
| `toolbaker` | list/run baked tools; suggestion tools only if adaptive on |
| `sheets` | Sheets, titleblocks, revisions, renumber |
| `materials` | Materials, appearance, assignment, takeoff |
| `geometry` | BBox, measure, clash, volume/area, … |
| `rooms` | Rooms/areas/spaces, finishes, separators |
| `links` | Revit/CAD links, coordinate audit, acquire/publish coordinates |
| `parameters` | Project/shared parameters |
| `organization` | Saved selections, view templates |
| `workflows` | Composite clash/audit/sheet/takeoff-style flows |
| `structural` | Columns, beams, foundations, rebar, loads, … |
| `kei` | Active KEI project DB path, query/write SQLite (WAL-safe), equipment import |

### send_code, ToolBaker, ribbon and languages

- **`revit_send_code_to_revit`** (on by default) compiles and runs a C# body inside Revit when no typed tool fits; `--read-only` or `--disable-toolbaker` removes it. See [docs/send-code.md](docs/send-code.md), and [docs/stairs-workflow.md](docs/stairs-workflow.md) for stairs.
- **ToolBaker:** `revit_list_baked_tools` / `revit_run_baked_tool` need `--toolsets toolbaker`. Adaptive bake (`--enable-adaptive-bake`, off by default) suggests tools from repeated calls; nothing is added until you accept one. Bake compiles inside Revit — no Visual Studio needed. See [docs/bake.md](docs/bake.md).
- **Ribbon:** start or stop the connection, open **History** to search and re-run past calls, and switch completion **Toasts** (on by default).
- **UI languages:** the add-in UI comes in 15 languages and follows Revit's UI language; change it with the **Language** combo in the ribbon slide-out. Tool names and payloads stay English. See [docs/localization.md](docs/localization.md).

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
| Completion toast (default on) | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| UI language (add-in) | ribbon **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

After changing server flags, restart the MCP connection so the client picks up the new tool list.

---

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Full Revit desktop only; Revit Viewer is not a supported target. CI builds all six add-ins, but runtime depth varies by year — recheck baked tools and custom C# on the years you use.

---

## Security and privacy

- Local by default: loopback TCP or a local named pipe, with a per-session auth token in the discovery files under `%LOCALAPPDATA%\RvtMcp\`.
- Tool arguments are schema-checked before handlers run; errors returned to the model are sanitized.
- `send_code` runs arbitrary C# in the Revit process — powerful and risky. Use `--read-only` or `--disable-toolbaker` if that is unacceptable.
- Adaptive bake, body cache and send_code journals are opt-in and stay under your user profile; defaults do not write raw send_code bodies to long-lived logs.

More: [SECURITY.md](SECURITY.md), [docs/bake.md](docs/bake.md).

---

## Docs

| Doc | Topic |
|-----|--------|
| [AGENTS.md](AGENTS.md) | Agent install protocol |
| [docs/install.md](docs/install.md) | Installer details, upgrade, uninstall, developer and NuGet installs |
| [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md) | Per-client MCP wiring |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Processes, transport, DTO rules |
| [docs/send-code.md](docs/send-code.md) | send_code source forms and failure handling |
| [docs/bake.md](docs/bake.md) | Adaptive bake and body privacy |
| [docs/localization.md](docs/localization.md) | UI languages, overrides, hot reload |
| [docs/roadmap.md](docs/roadmap.md) | Near-term hardening and non-goals |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | KEI SQLite tools (`--toolsets kei`) |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Build, test, add a tool |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

---

## Community contributions

Bug reports, reproducible examples and proposals help improve rvt-mcp. Thanks to:

| Contributor | Contribution |
|-------------|--------------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | Reproducible reports on MEP network membership and pipe system handling ([#11](https://github.com/bimwright/rvt-mcp/issues/11), [#12](https://github.com/bimwright/rvt-mcp/issues/12)). |
| [@razmikb](https://github.com/razmikb) | Hosted-family placement and stair/send-code failure reports that led to placement checks, helper-class support and broader failure-handling tests ([#13](https://github.com/bimwright/rvt-mcp/issues/13), [#14](https://github.com/bimwright/rvt-mcp/issues/14)). |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | Family Authoring Tool Suite proposal that helped clarify the roadmap and scope ([#7](https://github.com/bimwright/rvt-mcp/issues/7)). |

---

## bimwright

Open-source tools connecting AI assistants to BIM and CAD applications.

The name **bimwright** combines **BIM** with **wright**, an old word for a maker or builder—as in *shipwright*.

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — Vietnamese-first BIM notes  

---

## License

Apache-2.0 — [LICENSE](LICENSE).

Forks and rebrands are welcome — the license terms are all that's required (keep `LICENSE` and the copyright notices, mark changed files). If rvt-mcp helped you, a star or a mention of BIMwright in your product is appreciated but entirely optional. Issues and PRs are always welcome.

Revit and Autodesk are trademarks of Autodesk, Inc. bimwright is independent and not affiliated with Autodesk.
