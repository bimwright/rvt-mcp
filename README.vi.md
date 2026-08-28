<!-- mcp-name: io.github.bimwright/rvt-mcp -->

<p align="center">
  <img src="https://raw.githubusercontent.com/bimwright/.github/master/assets/logos/rvt-mcp.png" alt="rvt-mcp" width="180" />
</p>

<h1 align="center">rvt-mcp</h1>

<p align="center">
  Cổng MCP cho Autodesk Revit — tool local cho agent, bake cá nhân tùy chọn
</p>

<p align="center">
  <a href="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml"><img src="https://github.com/bimwright/rvt-mcp/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/Revit-2022--2027-186BFF" alt="Revit 2022-2027" /></a>
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-229%20tools-6C47FF" alt="MCP tools" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · Tiếng Việt · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>
</p>

---

## Cài đặt

Máy client nên dùng setup ZIP từ [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest). Gói gồm MCP server self-contained và plugin Revit 2022–2027 — không cần .NET SDK, NuGet global tool, hay clone source.

**AI agent:** tải `RvtMcp.Setup-*-win-x64.zip` từ latest release rồi chạy installer. **Không** clone, build, hay `dotnet tool install` trừ khi user yêu cầu bản developer. Protocol: [AGENTS.md](AGENTS.md).

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

Installer tìm Revit 2022–2027, cài plugin khớp năm, copy server vào `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\`, và nối MCP client đã có. Ghi đè bằng `-Client codex|opencode|claude|kilo|none`.

**Không** cài ZIP v0.5.0 trở về trước, và **không** `dotnet tool install -g Bimwright.Rvt.Server` / `RvtMcp.Server`.

AutoCAD: dùng [dwg-mcp](https://github.com/bimwright/dwg-mcp) riêng — product khác, cài riêng.

### Kiểm tra đã chạy

1. Mở Revit với một model.
2. Bật kết nối MCP trên ribbon (panel BIMwright / RvtMcp).
3. Từ MCP client: list tools, gọi `revit_get_current_view_info`.

Kỳ vọng dạng:

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

Lỗi thì coi như chưa cài xong — sửa config client / load plugin trước.

### Gỡ cài

Từ setup ZIP (hoặc script trong repo):

```powershell
# Setup ZIP:
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes

# Clone repo:
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-all.ps1 -Yes
```

Gỡ plugin, server self-contained, entry client, discovery, log, cache ToolBaker.

### Cài developer

```powershell
git clone https://github.com/bimwright/rvt-mcp.git
cd rvt-mcp
dotnet build src/RvtMcp.sln -c Debug
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -SourceDir . -Client none
```

Đóng hết Revit trước — build sẽ deploy DLL plugin vào `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\`. `install.ps1` tìm Revit 2022–2027, copy server vào `%LOCALAPPDATA%\RvtMcp\rvt\server\<version>\`, và nối MCP client đã có (`-Client codex|opencode|claude|kilo|none`, `-Years 2024` nếu chỉ một năm).

Đừng `dotnet tool install` `RvtMcp.Server` hay `Bimwright.Rvt.Server` — đó không còn là đường cài hiện tại.

### Migration từ `Bimwright.Rvt.*` (v0.3 trở về trước)

v0.4+ đổi package/folder sang `RvtMcp.*` (repo và brand bimwright giữ nguyên).

1. Đóng hết Revit.
2. `pwsh scripts/uninstall-old.ps1` — xóa plugin cũ `%APPDATA%\…\Bimwright\` và server root cũ; giữ bake/journal user, migrate sang `%LOCALAPPDATA%\RvtMcp\` lần chạy mới đầu.
3. Cài ZIP GitHub Release hiện tại (mục Cài đặt ở trên). Đừng cài package v0.5.0 trở về trước.
4. MCP client dùng entry **`rvt-mcp`** (entry cũ theo năm `bimwright-rvt-r22`… bị installer gỡ).

---

## Đây là gì

`rvt-mcp` là cầu **local** giữa MCP client (Claude, Cursor, Codex, OpenCode, …) và một session Revit đang chạy.

Hai process:

| Thành phần | Vai trò |
|------------|---------|
| **RvtMcp.Server** | MCP server .NET 8 trên stdio. Không reference Revit — build được mọi máy. |
| **RvtMcp.Plugin** | Add-in mỏng theo năm Revit (2022–2027). Chạy trong Revit, thực thi trên UI thread. |

Agent → MCP → server → localhost TCP (≤2024) hoặc Named Pipe (≥2025) → plugin → Revit API.

Mọi thứ trên máy user. Gateway không bắt buộc cloud relay.

Không có sidecar Node/TypeScript. Server, plugin, handler, ToolBaker đều C#. Code lệnh dùng chung trong `src/shared/`; mỗi năm là shell nhỏ + `#if` khi API lệch. Chi tiết: [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Vì sao có project này

Người dùng Revit thường đã biết mình muốn tự động hóa cái gì. Khó là đưa ý tưởng thành phần mềm: học đủ C#/Dynamo, vật lộn API, đóng gói add-in, sống sót qua upgrade — hoặc thuê ngoài, hoặc mua tool cứng chỉ khớp một nửa văn phòng.

Agent thay nửa đầu vòng lặp (mô tả việc, thử live). Chúng không xóa transaction, đơn vị, selection, worksharing, hay “vừa phá model chưa?”. Gateway này lo phần đó: **bề mặt tool typed** cho việc thường gặp, escape hatch khi cần C# ad-hoc trong Revit, và đường **tùy chọn** biến pattern local lặp lại thành tool cá nhân (ToolBaker).

Không phải add-in “một cho mọi hãng”. Văn phòng khác nhau. Cược: runtime chung, tool *của bạn* mọc phía trên.

**Phạm vi (thành thật):** không đúc MCP tool mới cho mọi edge case. Có typed tool thì dùng; còn lại `revit_send_code_to_revit` (chỉ C#). Quản lý family **trong project** có; suite authoring Family Editor đầy đủ và host Revit Viewer hiện **ngoài scope** — xem [docs/roadmap.md](docs/roadmap.md).

---

## Session thường trông thế nào

1. Revit mở model; plugin đã connect (ribbon).
2. MCP client start `rvt-mcp` / server đã cài.
3. Agent gọi tool: query view/selection, tạo grid/room, sheet, MEP, export, … Độ dài **mm** ở biên tool.
4. Nhiều write một bước undo: `revit_batch_execute`.
5. Nhiều Revit: `revit_list_available_targets` rồi `revit_switch_target` với năm 4 chữ số (`2024`, không `R24`).

Khi không có typed tool phù hợp:

```text
revit_send_code_to_revit   # body C#, compile + chạy trong plugin
```

Tool này bật mặc định (toolset `meta`). Tắt bằng `--read-only` hoặc `--disable-toolbaker` nếu không muốn agent compile code trong model.

### ToolBaker (tùy chọn)

Mặc định có `revit_send_code_to_revit`. `revit_list_baked_tools` / `revit_run_baked_tool` cần `--toolsets toolbaker` (hoặc `--toolsets all`).

**Adaptive bake** (gợi ý tool mới từ usage) **tắt** trừ khi bật. Khi bật, pattern lặp có thể hiện qua `revit_list_bake_suggestions`; accept/dismiss do bạn. Không tự lên ribbon nếu chưa accept.

Cờ hay dùng (JSON / env — [Cấu hình](#cấu-hình)):

| Mục tiêu | Bật gì |
|----------|--------|
| Học từ lời gọi **typed** lặp | `--enable-adaptive-bake` |
| Cluster thêm body **`send_code`** | thêm `--cache-send-code-bodies` (đã redact; vẫn local) |
| Journal disk ngắn hạn body send_code | `persistSendCodeBodies` + TTL (mặc định privacy: tắt) |

Bake compile **trong Revit** qua Roslyn — end user không cần Visual Studio. Chi tiết & privacy: [docs/bake.md](docs/bake.md).

### Toast (tùy chọn)

Toast hoàn thành trong Revit **tắt** mặc định. Bật bằng nút ribbon **Toast**, `enableToast` trong config, hoặc `BIMWRIGHT_ENABLE_TOAST=1`. Chỉ hiện call **đã xong** (không toast “đang chạy”). Capture thành công có thể có thumbnail nếu file trong path allowlist. Ribbon **Status** cũng in toast + cờ bake/privacy để khỏi đoán.

---

## Kiến trúc (ngắn)

```text
MCP client (stdio)
    → RvtMcp.Server (.NET 8)
        → TCP (Revit 2022–2024) hoặc Named Pipe (2025–2027)
            → Plugin shell (theo năm)
                → ExternalEvent → Revit API / transaction / undo
```

Handler trả DTO thuần — không serialize object Revit sống trên wire.

---

## Tools

Số lượng (chưa tính baked tool cá nhân):

| Mode | Tools | Ghi chú |
|------|------:|---------|
| Default | **40** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **229** | Full catalog |
| `all` + adaptive bake | **232** | Thêm 3 tool vòng đời suggestion |

Tên MCP: `revit_*`. Tên wire server↔plugin: snake_case không prefix.

**Toolset bật mặc định:** `query`, `create`, `view`, `meta`

**Tắt trừ khi bật:** mọi toolset còn lại (`export`, `geometry`, `mep`, `structural`, `toolbaker`, `modify`, `delete`, …).  
Ví dụ: `--toolsets query,view,meta` hoặc `--toolsets all`.  
`--read-only` gỡ mọi toolset write-capable (kể cả `create`).

| Toolset | Phạm vi | Default |
|---------|---------|---------|
| `query` | View, selection, filter, stats, param, quan hệ, workset, group/assembly | on |
| `create` | Grid, level, room, element line/point/surface, group | on |
| `view` | Tạo view, layout sheet, capture, crop/scale | on |
| `meta` | Batch (tối đa 20), multi-Revit target, project info, purge (MVP), message, send_code | on |
| `lint` | Pattern đặt tên view, firm-profile, tóm tắt warning | off |
| `schedule` | List/tạo schedule, field, formula, data | off |
| `families` | Load/unload, type, instance, audit, export `.rfa` (phía project) | off |
| `modify` | Operate/color, set param, đổi type, gán workset | off |
| `delete` | Xóa theo id | off |
| `annotation` | Tag, text, dim, region, keynote, check | off |
| `export` | PDF/DWG/IFC/NWC, room data, và helper export khác | off |
| `mep` | System, connector, network, place terminal/fixture, … | off |
| `graphics` | View filter, override, visibility/phase | off |
| `toolbaker` | list/run baked; suggestion chỉ khi adaptive on | off |
| `sheets` | Sheet, titleblock, revision, renumber | off |
| `materials` | Material, appearance, gán, takeoff | off |
| `geometry` | BBox, measure, clash, volume/area, … | off |
| `rooms` | Room/area/space, finish, separator | off |
| `links` | Link Revit/CAD, audit tọa độ, acquire/publish coordinates | off |
| `parameters` | Project/shared parameter | off |
| `organization` | Saved selection, view template | off |
| `workflows` | Flow ghép clash/audit/sheet/takeoff | off |
| `structural` | Column, beam, foundation, rebar, load, … | off |
| `kei` | DB project KEI, query/write SQLite (WAL-safe), import equipment | off |

### Tool đại diện

Không dump 200+ schema — neo agent/người hay dùng:

| Toolset | Tool | Việc |
|---------|------|------|
| `query` | `revit_get_current_view_info` | View active: type, level, scale |
| `query` | `revit_get_selected_elements` | Selection hiện tại |
| `query` | `revit_ai_element_filter` | Filter category + param (mm) |
| `query` | `revit_get_element_details` | Location, bbox, workset, phase, … |
| `create` | `revit_create_grid` / `revit_create_level` / `revit_create_room` | Layout cơ bản |
| `create` | `revit_create_point_based_element` | Cửa, furniture, … từ type id |
| `view` | `revit_capture_view_image` | Capture raster (path allowlist) |
| `meta` | `revit_batch_execute` | Một `TransactionGroup` nhiều lệnh |
| `meta` | `revit_list_available_targets` / `revit_switch_target` | Multi-Revit |
| `families` | `revit_load_family_from_path` | Load `.rfa` vào project |
| `links` | `revit_get_project_coordinate_system` / `revit_get_link_coordinate_system` | Gốc host/link, site, transform, True North |
| `toolbaker` | `revit_send_code_to_revit` | Escape hatch (C#) |
| `toolbaker` | `revit_list_baked_tools` / `revit_run_baked_tool` | Tool cá nhân đã accept |
| `toolbaker` | `revit_list_bake_suggestions` | Chỉ adaptive |
| `lint` | `revit_analyze_view_naming_patterns` | Outlier đặt tên |

Snapshot golden trong test khóa surface; lệch count thì tin test/code.

---

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Compile matrix 6 shell. Runtime sâu vẫn lệch theo năm — bake và C# custom nên kiểm lại năm bạn quan tâm.

**Host:** chỉ Revit desktop đầy đủ. Revit Viewer **không** hỗ trợ.

---

## Bảo mật và privacy

- Transport local mặc định (loopback TCP / named pipe local).
- File discovery dưới `%LOCALAPPDATA%\RvtMcp\` có auth token theo session.
- Argument tool được schema-check trước handler.
- Lỗi trả về model được sanitize (giảm lộ path).
- `send_code` chạy C# tùy ý trong process Revit — mạnh và rủi ro; tắt toolbaker nếu không chấp nhận.
- Adaptive bake, body cache, journal TTL là **opt-in**, nằm dưới profile user. Mặc định không ghi raw send_code body vào log dài hạn.

Thêm: [SECURITY.md](SECURITY.md), [docs/bake.md](docs/bake.md).

---

## Cấu hình

Ưu tiên, cao thắng: **CLI → env (`BIMWRIGHT_*`) →** `%LOCALAPPDATA%\RvtMcp\rvtmcp.config.json`.

| Setting | CLI | Env | JSON |
|---------|-----|-----|------|
| Năm target | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| LAN bind (plugin) | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker surface | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache body send_code (cluster bake) | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| Journal persist send_code | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| TTL journal | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| Toast hoàn thành | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=1` | `enableToast` |

Đổi cờ server xong: restart kết nối MCP để client nhận tool list mới.

---

## MCP clients

| Client | Wiring |
|--------|--------|
| Claude Code | project `.mcp.json` hoặc `~/.claude.json` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` |
| OpenCode / Codex / Kilo | `install.ps1 -Client …` (script) |
| Cursor / Cline / VS Code Copilot | JSON layout đã document |
| Gemini CLI / Antigravity | `gemini mcp add` hoặc settings JSON |

Installer auto-detect thường đủ; xem [AGENTS.md](AGENTS.md) và `docs/mcp-config-*.md` khi sửa tay.

---

## Repo layout

```text
rvt-mcp/
├── src/
│   ├── RvtMcp.sln
│   ├── server/            # MCP server
│   ├── shared/            # Handlers, transport, ToolBaker, toast, …
│   ├── plugin-r22/ … r27/ # Một shell mỗi năm Revit
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

Đóng Revit trước khi build plugin (DLL lock). Plugin deploy vào `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` khi build Debug/Release thường.

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

Quy ước đóng góp / snapshot: [CONTRIBUTING.md](CONTRIBUTING.md).

### Độ chín

Dùng được, không thần thánh. CI build 6 shell + test server. Runtime sâu nhất ở năm giữa dải; model production hãy cẩn thận và verify trên *build Revit của bạn*. Checklist máy mới: [docs/testing/fresh-install-checklist.md](docs/testing/fresh-install-checklist.md).

---

## Tài liệu thêm

| Doc | Chủ đề |
|-----|--------|
| [AGENTS.md](AGENTS.md) | Protocol cài cho agent |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Process, transport, DTO |
| [docs/bake.md](docs/bake.md) | Adaptive bake và privacy body |
| [docs/roadmap.md](docs/roadmap.md) | Hardening gần và non-goal |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | Tool KEI SQLite (`--toolsets kei`) |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

---

## bimwright

Cùng house style trên các host AEC:

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — Kho BIM ưu tiên tiếng Việt  

---

## License

Apache-2.0 — [LICENSE](LICENSE).

Revit và Autodesk là trademark của Autodesk, Inc. bimwright độc lập, không liên kết Autodesk.
