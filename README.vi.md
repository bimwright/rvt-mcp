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
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · Tiếng Việt · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>
</p>

---

## Đây là gì

`rvt-mcp` là cầu nối **local** giữa MCP client và một session Revit đang chạy. Server .NET 8 nói MCP qua stdio; mỗi năm Revit (2022–2027) có một add-in mỏng chạy trong Revit, kết nối qua localhost TCP (≤2024) hoặc named pipe (≥2025). Mọi thứ nằm trên máy, toàn bộ bằng C#, độ dài ở biên tool tính bằng mm. Chi tiết: [ARCHITECTURE.md](ARCHITECTURE.md).

Agent có **bộ tool typed** cho việc Revit thường gặp, escape hatch C# cho phần còn lại, và đường **tùy chọn** biến pattern lặp lại thành tool cá nhân (ToolBaker): bắt đầu từ một runtime chung rồi phát triển tool *của bạn* phía trên. Family Editor authoring hiện nằm ngoài phạm vi ([roadmap](docs/roadmap.md)).

---

## Cài đặt

Dùng setup ZIP từ [GitHub Releases](https://github.com/bimwright/rvt-mcp/releases/latest): server self-contained cùng add-in Revit 2022–2027, không cần .NET SDK hay clone source. **AI agent:** làm theo [AGENTS.md](AGENTS.md), không clone hay build trừ khi user yêu cầu bản developer.

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$zip = "$env:TEMP\RvtMcp.Setup-$tag-win-x64.zip"
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $dir -Force

powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

Đóng Revit trước. Installer tìm các bản Revit 2022–2027 có `Revit.exe`, cài add-in khớp năm và server vào `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe`, kiểm tra cả hai và rollback nếu lỗi. Installer không đụng config MCP client. Chi tiết và các cách cài khác (developer, chỉ server NuGet): [docs/install.md](docs/install.md).

### Kết nối MCP client

Đăng ký một stdio server tên `rvt-mcp` với command là đường dẫn server ở trên (viết thành đường dẫn tuyệt đối), bằng lệnh `mcp add`, giao diện cài đặt hoặc file config của chính client. Mọi MCP client stdio đều dùng được (Claude Code, Claude Desktop, Codex, Cursor, VS Code, Gemini CLI, OpenCode, Kilo, …). Các bước đã kiểm chứng cho từng client: [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md).

### Kiểm tra đã chạy

1. Mở Revit với một model.
2. Bật kết nối MCP trên ribbon (tab **Add-Ins** → panel **RvtMcp**).
3. Từ MCP client: list tools, gọi `revit_get_current_view_info`.

Kỳ vọng dạng:

```json
{ "viewName": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1", "scale": 100 }
```

Lỗi thì coi như chưa cài xong — sửa config client hoặc việc load add-in trước.

### Cập nhật

Đóng Revit và MCP client, giải nén ZIP bản mới vào một thư mục mới, rồi chạy `install.ps1 -WhatIf` của nó, sau đó `install.ps1` — không gỡ cài trước. Đường dẫn server không đổi nên client chỉ cần khởi động lại. Nâng cấp từ v0.6.2 trở về trước? Trỏ client sang đường dẫn `current` ở trên, rồi xóa các thư mục server cũ bằng `install.ps1 -PruneOldServers`. Thêm: [docs/install.md](docs/install.md#upgrade).

### Gỡ cài

Từ thư mục setup ZIP:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Yes
```

Gỡ add-in và server nhưng giữ cài đặt, bản dịch, dữ liệu ToolBaker và log, trừ khi thêm `-Purge`. Tự xóa entry `rvt-mcp` trong MCP client. Thêm: [docs/install.md](docs/install.md#uninstall).

---

## Tools

| Mode | Tools | Ghi chú |
|------|------:|---------|
| Default | **40** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **229** | Full catalog |
| `all` + adaptive bake | **232** | Thêm 3 tool vòng đời suggestion |

Số lượng chưa tính baked tool cá nhân. Các toolset khác tắt cho tới khi bạn bật, ví dụ `--toolsets query,view,meta,mep` hoặc `--toolsets all`; `--read-only` gỡ mọi toolset write-capable (kể cả `create`).

| Toolset | Phạm vi |
|---------|---------|
| `query` | View, selection, filter, stats, param, quan hệ, workset, group/assembly |
| `create` | Grid, level, room, element line/point/surface, group |
| `view` | Tạo view, layout sheet, capture, crop/scale |
| `meta` | Batch (tối đa 20), multi-Revit target, project info, purge (MVP), message, send_code |
| `lint` | Pattern đặt tên view, firm-profile, tóm tắt warning |
| `schedule` | List/tạo schedule, field, formula, data |
| `families` | Load/unload, type, instance, audit, export `.rfa` (phía project) |
| `modify` | Operate/color, set param, đổi type, gán workset |
| `delete` | Xóa theo id |
| `annotation` | Tag, text, dim, region, keynote, check |
| `export` | PDF/DWG/IFC/NWC, room data, và helper export khác |
| `mep` | System, connector, network, place terminal/fixture, … |
| `graphics` | View filter, override, visibility/phase |
| `toolbaker` | list/run baked; suggestion chỉ khi adaptive on |
| `sheets` | Sheet, titleblock, revision, renumber |
| `materials` | Material, appearance, gán, takeoff |
| `geometry` | BBox, measure, clash, volume/area, … |
| `rooms` | Room/area/space, finish, separator |
| `links` | Link Revit/CAD, audit tọa độ, acquire/publish coordinates |
| `parameters` | Project/shared parameter |
| `organization` | Saved selection, view template |
| `workflows` | Flow ghép clash/audit/sheet/takeoff |
| `structural` | Column, beam, foundation, rebar, load, … |
| `kei` | DB project KEI, query/write SQLite (WAL-safe), import equipment |

### send_code, ToolBaker, ribbon và ngôn ngữ

- **`revit_send_code_to_revit`** (bật mặc định) compile và chạy body C# trong Revit khi không có typed tool phù hợp; `--read-only` hoặc `--disable-toolbaker` sẽ gỡ nó. Xem [docs/send-code.md](docs/send-code.md), và [docs/stairs-workflow.md](docs/stairs-workflow.md) cho cầu thang.
- **ToolBaker:** `revit_list_baked_tools` / `revit_run_baked_tool` cần `--toolsets toolbaker`. Adaptive bake (`--enable-adaptive-bake`, mặc định tắt) gợi ý tool từ các lời gọi lặp lại; không có gì được thêm cho tới khi bạn accept. Bake compile ngay trong Revit — không cần Visual Studio. Xem [docs/bake.md](docs/bake.md).
- **Ribbon:** bật/tắt kết nối, mở **History** để tìm và chạy lại các lời gọi trước, và bật/tắt **Toast** hoàn thành (mặc định bật).
- **Ngôn ngữ giao diện:** UI của add-in có 15 ngôn ngữ và theo ngôn ngữ giao diện của Revit; đổi bằng combo **Language** trong slide-out của ribbon. Tên tool và payload vẫn là tiếng Anh. Xem [docs/localization.md](docs/localization.md).

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
| Toast hoàn thành (mặc định bật) | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| Ngôn ngữ UI (add-in) | ribbon **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

Đổi cờ server xong: restart kết nối MCP để client nhận tool list mới.

---

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Chỉ Revit desktop đầy đủ; Revit Viewer không được hỗ trợ. CI build cả 6 add-in, nhưng độ sâu runtime khác nhau theo năm — kiểm lại baked tool và C# custom trên các năm bạn dùng.

---

## Bảo mật và privacy

- Mặc định local: loopback TCP hoặc named pipe local, kèm auth token theo session trong các file discovery dưới `%LOCALAPPDATA%\RvtMcp\`.
- Argument tool được schema-check trước khi handler chạy; lỗi trả về model được sanitize.
- `send_code` chạy C# tùy ý trong process Revit — mạnh và rủi ro. Dùng `--read-only` hoặc `--disable-toolbaker` nếu không chấp nhận được.
- Adaptive bake, body cache và journal send_code đều opt-in và nằm dưới profile user; mặc định không ghi raw body send_code vào log dài hạn.

Thêm: [SECURITY.md](SECURITY.md), [docs/bake.md](docs/bake.md).

---

## Tài liệu

| Doc | Chủ đề |
|-----|--------|
| [AGENTS.md](AGENTS.md) | Protocol cài cho agent |
| [docs/install.md](docs/install.md) | Chi tiết installer, cập nhật, gỡ cài, cài developer và NuGet |
| [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md) | Nối MCP theo từng client |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Process, transport, DTO |
| [docs/send-code.md](docs/send-code.md) | Dạng source và xử lý lỗi của send_code |
| [docs/bake.md](docs/bake.md) | Adaptive bake và privacy body |
| [docs/localization.md](docs/localization.md) | Ngôn ngữ UI, override, hot reload |
| [docs/roadmap.md](docs/roadmap.md) | Hardening gần và non-goal |
| [docs/kei-equipment-import.md](docs/kei-equipment-import.md) | Tool KEI SQLite (`--toolsets kei`) |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Build, test, thêm tool |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

---

## Đóng góp từ cộng đồng

Báo lỗi, ví dụ tái hiện và đề xuất giúp cải thiện rvt-mcp. Cảm ơn:

| Người đóng góp | Đóng góp |
|----------------|----------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | Báo lỗi kèm ví dụ tái hiện về thành phần mạng MEP và xử lý hệ thống ống ([#11](https://github.com/bimwright/rvt-mcp/issues/11), [#12](https://github.com/bimwright/rvt-mcp/issues/12)). |
| [@razmikb](https://github.com/razmikb) | Báo lỗi đặt family có host và xử lý lỗi cầu thang/send-code, giúp bổ sung kiểm tra vị trí, hỗ trợ lớp helper và mở rộng kiểm thử xử lý lỗi ([#13](https://github.com/bimwright/rvt-mcp/issues/13), [#14](https://github.com/bimwright/rvt-mcp/issues/14)). |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | Đề xuất Family Authoring Tool Suite giúp làm rõ lộ trình và phạm vi sản phẩm ([#7](https://github.com/bimwright/rvt-mcp/issues/7)). |

---

## bimwright

Các công cụ mã nguồn mở kết nối trợ lý AI với ứng dụng BIM và CAD.

Tên **bimwright** ghép **BIM** với **wright**, một từ tiếng Anh cổ chỉ người thợ chế tạo hoặc xây dựng — như trong *shipwright* (thợ đóng tàu).

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — Kho BIM ưu tiên tiếng Việt  

---

## License

Apache-2.0 — [LICENSE](LICENSE).

Bạn cứ tự do fork và đổi thương hiệu — chỉ cần tuân thủ điều khoản license (giữ `LICENSE` và các copyright notice, ghi chú file đã sửa). Nếu rvt-mcp có ích cho bạn, một star hoặc lời nhắc tới BIMwright trong sản phẩm của bạn là điều tác giả rất trân trọng, nhưng hoàn toàn tùy ý. Luôn chào đón issue và PR.

Revit và Autodesk là trademark của Autodesk, Inc. bimwright độc lập, không liên kết Autodesk.
