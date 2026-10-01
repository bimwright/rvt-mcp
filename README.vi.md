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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-227%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · Tiếng Việt · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>
</p>

---

## Đây là gì

`rvt-mcp` là cầu nối **local** giữa MCP client và một session Revit đang chạy. Server .NET 8 nói MCP qua stdio; mỗi năm Revit (2022–2027) có một add-in mỏng chạy trong Revit, kết nối qua localhost TCP (≤2024) hoặc named pipe (≥2025). Mọi thứ nằm trên máy, toàn bộ bằng C#, độ dài ở biên tool tính bằng mm. Chi tiết: [ARCHITECTURE.md](ARCHITECTURE.md).

Agent có **bộ tool typed** cho việc Revit thường gặp, escape hatch C# cho phần còn lại, và đường **tùy chọn** biến pattern lặp lại thành tool cá nhân (ToolBaker): bắt đầu từ một runtime chung rồi phát triển tool *của bạn* phía trên. Family Editor authoring hiện nằm ngoài phạm vi.

---

## Cài đặt

**Người dùng: để AI agent cài giúp.** Bạn không cần tự chạy gì. Copy dòng dưới, dán vào AI agent của bạn (Claude Code, Codex, Cursor, …) rồi đi pha cà phê trong lúc agent làm việc. Agent làm theo [AGENTS.md](AGENTS.md) và hỏi bạn trước khi cài hay sửa config client.

```text
Cài rvt-mcp giúp tôi: https://github.com/bimwright/rvt-mcp
```

**Hoặc tự chạy installer.** Đóng Revit, rồi trong PowerShell:

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile "$dir.zip"
Expand-Archive "$dir.zip" -DestinationPath $dir -Force
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1"
```

Một lần chạy cài cả hai phía: add-in cho mọi bản Revit 2022–2027 trên máy, và entry `rvt-mcp` trong mọi MCP client tìm thấy (mỗi config được backup trước). Thêm `-WhatIf` để xem trước, `-Client claude,cursor` để chỉ nối các client đó, hoặc `-Client none` để không đụng config client và tự đăng ký server — nằm ở `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe` ([các bước cho từng client](docs/mcp-client-wiring.md)).

**Kiểm tra:** khởi động lại AI client, mở một model trong Revit, bật MCP trên ribbon (**Add-Ins** → **RvtMcp**) rồi nhờ agent gọi `revit_get_current_view_info`. Kết quả phải là tên và loại của view đang mở.

**Cập nhật:** chạy installer của bản mới theo cùng cách — không cần gỡ trước; client chỉ cần khởi động lại. **Gỡ cài:** `uninstall.ps1 -Yes` trong cùng thư mục gỡ add-in và server (cài đặt được giữ, trừ khi thêm `-Purge`); chạy `install.ps1 -Uninstall -Client auto` trước nếu muốn xóa luôn entry trong client. Thêm, gồm cài developer và NuGet: [docs/install.md](docs/install.md).

---

## Video

Video cộng đồng giới thiệu rvt-mcp. Cách cài trong video có thể cũ hơn README này — hãy làm theo các bước ở trên.

- [Connecting ChatGPT Astra to Revit | Testing AI Changes in a Real Project](https://www.youtube.com/watch?v=J-i057B-3dI) — Revit Mentor
- [Exploring Revit + GPT 6 Astra](https://www.youtube.com/watch?v=2_W1uLn6s_I) — BIM Pure
- [GPT-6 Astra Built a Revit House in 14 Minutes](https://www.youtube.com/watch?v=pkFnSQ9Bapg) — Archi Vlogs

---

## Tools

| Mode | Tools | Ghi chú |
|------|------:|---------|
| Fresh install | **227** | `install.ps1` seeds `"toolsets": ["all"]` trong `rvtmcp.config.json` |
| Bare `rvt-mcp.exe` | **42** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **227** | Full catalog |
| `all` + adaptive bake | **230** | Thêm 3 tool vòng đời suggestion |

Số lượng chưa tính baked tool cá nhân. Installer chỉ seed khi `rvtmcp.config.json` chưa có `toolsets` — list của bạn sống sót qua upgrade; bỏ key (hoặc đặt CSV riêng) thì bare server về mặt 42 tool. Read-only lọc từng tool theo `ReadOnly=true`, nên vẫn giữ công cụ đọc trong nhóm hỗn hợp. Tool có thể ghi file sẽ bị loại, kể cả khi output mặc định là inline.

| Toolset | Phạm vi |
|---------|---------|
| `query` | View, selection, filter, stats, param, quan hệ, workset, group/assembly |
| `create` | Grid, level, room, element line/point/surface, group |
| `view` | Tạo view, layout sheet, capture, crop/scale |
| `meta` | Batch (tối đa 20), multi-Revit target, recent model, project info, purge (MVP), message, send_code |
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

### send_code, ToolBaker, ribbon và ngôn ngữ

- **`revit_send_code_to_revit`** (bật mặc định) compile và chạy body C# trong Revit khi không có typed tool phù hợp; `--read-only` hoặc `--disable-send-code` sẽ gỡ nó. Xem [docs/send-code.md](docs/send-code.md), và [docs/stairs-workflow.md](docs/stairs-workflow.md) cho cầu thang.
- **ToolBaker:** `revit_list_baked_tools` / `revit_run_baked_tool` cần `--toolsets toolbaker`. Adaptive bake (`--enable-adaptive-bake`, mặc định tắt) gợi ý tool từ các lời gọi lặp lại; không có gì được thêm cho tới khi bạn accept. Bake compile ngay trong Revit — không cần Visual Studio. Xem [docs/bake.md](docs/bake.md).
- **Ribbon:** bật/tắt kết nối, mở **History** để tìm và chạy lại các lời gọi trước, và bật/tắt **Toast** hoàn thành (mặc định bật).
- **Ngôn ngữ giao diện:** UI của add-in có 15 ngôn ngữ và theo ngôn ngữ giao diện của Revit; đổi bằng nút **Language** trong slide-out của ribbon — nút mở **Settings → tab General → nhóm Language** (`BIMWRIGHT_UI_LANGUAGE` vẫn thắng). Tên tool và payload vẫn là tiếng Anh. Xem [docs/localization.md](docs/localization.md).

### Vì sao có toast thông báo hoạt động?

Toast không chỉ để trang trí. Tính năng này xuất phát từ ba nhu cầu thực tế của người phát triển:

- **Giải phóng người dùng khỏi việc nhìn chat liên tục.** Khi quan sát người khác dùng MCP vào công việc thực tế, tôi thấy AI agent có thể làm khá lâu trong khi Revit không có phản hồi rõ ràng. Người dùng phải nhìn khung chat chỉ để biết AI có đang làm việc hay không. Việc chờ và theo dõi này chiếm sự chú ý không cần thiết. Toast báo kết quả sau mỗi lần gọi tool hoàn tất, để người dùng có thể chuyển sang việc khác giữa các lần cập nhật.
- **Hỗ trợ multitasking.** Tôi thường làm nhiều việc cùng lúc, tự triển khai và test lặp lại trên nhiều ứng dụng. Thông báo ngắn giúp theo dõi các phiên làm việc mà không phải giữ mắt ở từng khung chat.
- **Hiện đại hóa trải nghiệm.** Phản hồi ngay trong Revit giúp quá trình tự động hóa rõ ràng, dễ nắm bắt hơn mà không phải ngắt công việc bằng hộp thoại.

Toast báo **kết quả từng tool**, không phải thanh tiến độ bên trong tool đang chạy và cũng không có nghĩa toàn bộ yêu cầu đã hoàn thành. Các kết quả liên tiếp cập nhật chung một card; thông báo có thể chờ khi Revit bị thu nhỏ hoặc đang có hộp thoại modal. Người dùng vẫn cần kiểm tra kết quả công việc của agent.

Mỗi card ghi tên gateway và năm Revit (ví dụ `rvt-mcp 2022`), tên tool vừa chạy, và bộ đếm Success · Failed · Capture. Ảnh chụp được giữ trên card ít nhất 5 giây.

Toast **bật mặc định** và có thể tắt. Trong **Settings → Toast**, chọn thời gian tự ẩn (10/20/30/60 giây; mặc định 20) và **Show branding** (**tắt mặc định**); bật branding sẽ hiện wordmark khi hover. Lựa chọn có hiệu lực ngay và được lưu qua các lần khởi động Revit; không cần bật brand để nhận thông báo hoạt động.

---

## Cấu hình

Ưu tiên, cao thắng: **CLI → env (`BIMWRIGHT_*`) →** `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json`.

| Setting | CLI | Env | JSON |
|---------|-----|-----|------|
| Năm target | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| send_code | `--enable-send-code` / `--disable-send-code` | `BIMWRIGHT_ENABLE_SEND_CODE` | `enableSendCode` |
| Call log | `--enable-call-log` / `--disable-call-log` | `BIMWRIGHT_ENABLE_CALL_LOG` | `enableCallLog` |
| Response guard | `--enable-response-guard` / `--disable-response-guard` | `BIMWRIGHT_ENABLE_RESPONSE_GUARD` | `enableResponseGuard` |
| Warn bytes | `--response-warn-bytes` | `BIMWRIGHT_RESPONSE_WARN_BYTES` | `responseWarnBytes` |
| Strong warn bytes | `--response-strong-warn-bytes` | `BIMWRIGHT_RESPONSE_STRONG_WARN_BYTES` | `responseStrongWarnBytes` |
| Budget bytes | `--response-budget-bytes` | `BIMWRIGHT_RESPONSE_BUDGET_BYTES` | `responseBudgetBytes` |
| Transport cap | `--max-response-bytes` | `BIMWRIGHT_MAX_RESPONSE_BYTES` | `maxResponseBytes` |
| LAN bind (plugin) | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker surface | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache body send_code (cluster bake) | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| Journal persist send_code | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| TTL journal | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| Toast hoàn thành (mặc định bật) | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| Brand trên toast (mặc định tắt, có lưu) | Settings → Toast → **Show branding** | — | `showBranding` |
| Thời gian toast tự ẩn (mặc định 20 giây) | Settings → Toast → **Idle duration** | — | `toastIdleSeconds` |
| Ngôn ngữ UI (add-in) | ribbon **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

Đổi cờ server xong: restart kết nối MCP để client nhận tool list mới.

---

## Permissions & auto mode — quyền chạy tự động

Các controls này đã được triển khai trên nhánh phát triển cho v1.0.0. Gói v0.8.1 đã phát hành chưa có các switch mới và cách lọc read-only theo từng tool. Phải gọi `send_code` và `run_baked_tool` trực tiếp; `batch_execute` từ chối hai lệnh này.

Annotations mô tả tác động lên document/file của từng tool. Đổi selection, active view và zoom tạm thời được tính là read-only. `send_code` không có annotations: không đưa vào quyền tự động, và xác nhận từng lượt chạy code. Với Claude Code, chỉ sao chép allow list read-only bên dưới; không cho phép wildcard rộng `mcp__rvt-mcp__*`. List này ứng với `--toolsets all`; toolsets bạn chọn có thể công bố ít tool hơn.

<details>
<summary>Allow list read-only (sinh từ annotations)</summary>

<!-- BEGIN READ_ONLY_ALLOWLIST -->
```json
{
  "permissions": {
    "allow": [
      "mcp__rvt-mcp__revit_activate_view",
      "mcp__rvt-mcp__revit_ai_element_filter",
      "mcp__rvt-mcp__revit_analyze_geometry_complexity",
      "mcp__rvt-mcp__revit_analyze_mep_network",
      "mcp__rvt-mcp__revit_analyze_model_statistics",
      "mcp__rvt-mcp__revit_analyze_sheet_layout",
      "mcp__rvt-mcp__revit_analyze_structural_connections",
      "mcp__rvt-mcp__revit_analyze_usage_patterns",
      "mcp__rvt-mcp__revit_analyze_view_naming_patterns",
      "mcp__rvt-mcp__revit_audit_families",
      "mcp__rvt-mcp__revit_clash_detection",
      "mcp__rvt-mcp__revit_compute_element_area",
      "mcp__rvt-mcp__revit_compute_element_volume",
      "mcp__rvt-mcp__revit_detect_firm_profile",
      "mcp__rvt-mcp__revit_detect_system_elements",
      "mcp__rvt-mcp__revit_find_elements_in_volume",
      "mcp__rvt-mcp__revit_find_mep_disconnects",
      "mcp__rvt-mcp__revit_find_overlapping_elements",
      "mcp__rvt-mcp__revit_find_schedule_elements",
      "mcp__rvt-mcp__revit_find_undimensioned_elements",
      "mcp__rvt-mcp__revit_find_untagged_elements",
      "mcp__rvt-mcp__revit_get_assembly_members",
      "mcp__rvt-mcp__revit_get_available_family_types",
      "mcp__rvt-mcp__revit_get_current_target",
      "mcp__rvt-mcp__revit_get_current_view_info",
      "mcp__rvt-mcp__revit_get_element_bounding_box",
      "mcp__rvt-mcp__revit_get_element_centroid",
      "mcp__rvt-mcp__revit_get_element_details",
      "mcp__rvt-mcp__revit_get_element_geometry",
      "mcp__rvt-mcp__revit_get_element_parameters",
      "mcp__rvt-mcp__revit_get_element_relationships",
      "mcp__rvt-mcp__revit_get_family_instances",
      "mcp__rvt-mcp__revit_get_group_members",
      "mcp__rvt-mcp__revit_get_link_coordinate_system",
      "mcp__rvt-mcp__revit_get_link_elements",
      "mcp__rvt-mcp__revit_get_material_properties",
      "mcp__rvt-mcp__revit_get_material_quantities",
      "mcp__rvt-mcp__revit_get_mep_element_connectors",
      "mcp__rvt-mcp__revit_get_model_warnings_summary",
      "mcp__rvt-mcp__revit_get_panel_schedule",
      "mcp__rvt-mcp__revit_get_print_settings",
      "mcp__rvt-mcp__revit_get_project_coordinate_system",
      "mcp__rvt-mcp__revit_get_room_boundaries",
      "mcp__rvt-mcp__revit_get_room_openings",
      "mcp__rvt-mcp__revit_get_schedulable_fields",
      "mcp__rvt-mcp__revit_get_schedule_data",
      "mcp__rvt-mcp__revit_get_schedule_definition",
      "mcp__rvt-mcp__revit_get_schedule_formulas",
      "mcp__rvt-mcp__revit_get_selected_elements",
      "mcp__rvt-mcp__revit_get_structural_loads",
      "mcp__rvt-mcp__revit_get_system_inventory",
      "mcp__rvt-mcp__revit_get_titleblock_parameters",
      "mcp__rvt-mcp__revit_get_type_parameters",
      "mcp__rvt-mcp__revit_get_view_visibility",
      "mcp__rvt-mcp__revit_list_areas",
      "mcp__rvt-mcp__revit_list_assemblies",
      "mcp__rvt-mcp__revit_list_available_targets",
      "mcp__rvt-mcp__revit_list_baked_tools",
      "mcp__rvt-mcp__revit_list_export_settings",
      "mcp__rvt-mcp__revit_list_family_types_in_family",
      "mcp__rvt-mcp__revit_list_groups",
      "mcp__rvt-mcp__revit_list_keynotes",
      "mcp__rvt-mcp__revit_list_linked_cad",
      "mcp__rvt-mcp__revit_list_linked_models",
      "mcp__rvt-mcp__revit_list_loaded_families",
      "mcp__rvt-mcp__revit_list_materials",
      "mcp__rvt-mcp__revit_list_mep_systems",
      "mcp__rvt-mcp__revit_list_phases",
      "mcp__rvt-mcp__revit_list_project_parameter_bindings",
      "mcp__rvt-mcp__revit_list_project_parameters",
      "mcp__rvt-mcp__revit_list_rebar",
      "mcp__rvt-mcp__revit_list_recent_models",
      "mcp__rvt-mcp__revit_list_revisions",
      "mcp__rvt-mcp__revit_list_rooms",
      "mcp__rvt-mcp__revit_list_saved_selections",
      "mcp__rvt-mcp__revit_list_schedules",
      "mcp__rvt-mcp__revit_list_shared_parameters",
      "mcp__rvt-mcp__revit_list_sheets",
      "mcp__rvt-mcp__revit_list_titleblocks",
      "mcp__rvt-mcp__revit_list_view_filters",
      "mcp__rvt-mcp__revit_list_view_templates",
      "mcp__rvt-mcp__revit_list_worksets",
      "mcp__rvt-mcp__revit_load_selection",
      "mcp__rvt-mcp__revit_measure_distance_between_elements",
      "mcp__rvt-mcp__revit_project_point_onto_face",
      "mcp__rvt-mcp__revit_raycast_from_point",
      "mcp__rvt-mcp__revit_select_elements",
      "mcp__rvt-mcp__revit_show_element_in_view",
      "mcp__rvt-mcp__revit_show_message",
      "mcp__rvt-mcp__revit_suggest_view_name_corrections",
      "mcp__rvt-mcp__revit_switch_target",
      "mcp__rvt-mcp__revit_workflow_model_audit"
    ]
  }
}
```
<!-- END READ_ONLY_ALLOWLIST -->

</details>

send_code mặc định **bật**, độc lập với ToolBaker; call-log mặc định **tắt**. CLI ưu tiên hơn environment, rồi đến JSON. Cấu hình server đã xác thực được truyền sang plugin riêng cho từng request. Khi call-log tắt, server journal, plugin `mcp-calls.jsonl` và journal body send-code đều không ghi; History trong bộ nhớ vẫn hoạt động. Journal body cần đồng thời bật call-log và opt-in TTL riêng. `usage.jsonl` của ToolBaker là luồng riêng, theo cấu hình adaptive-bake.

Response guard mặc định **bật**: cảnh báo tại 65536 byte UTF-8, cảnh báo mạnh trên 262144, budget 716800 và transport cap 1048576 byte. Server đo cả JSON escaping, content và metadata MCP. Kết quả đọc quá lớn trả `RESPONSE_TOO_LARGE` cùng cách thu hẹp; lệnh ghi đã hoàn tất trả tóm tắt. Output code tùy ý được spill ra file cục bộ với `mutation_applied: null`; đọc file đó, không chạy lại lệnh. Tắt guard vẫn giữ transport cap. Các ngưỡng phải là số nguyên >=1024, theo `warn <= strong <= budget <= max`; khi giảm budget, giảm các ngưỡng cảnh báo tương ứng.

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Chỉ Revit desktop đầy đủ; Revit Viewer không được hỗ trợ. CI build cả 6 add-in, nhưng độ sâu runtime khác nhau theo năm — kiểm lại baked tool và C# custom trên các năm bạn dùng.

---

## Bảo mật và privacy

- Mặc định local: loopback TCP hoặc named pipe local, kèm auth token theo session trong các file discovery dưới `%LOCALAPPDATA%\Bimwright\rvt-mcp\`.
- Argument tool được schema-check trước khi handler chạy; lỗi trả về model được sanitize.
- `send_code` chạy C# tùy ý trong process Revit — mạnh và rủi ro. Dùng `--read-only` hoặc `--disable-send-code` nếu không chấp nhận được.
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
| [CONTRIBUTING.md](CONTRIBUTING.md) | Build, test, thêm tool |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

---

## Đóng góp từ cộng đồng

Mã nguồn, báo lỗi, ví dụ tái hiện và đề xuất giúp cải thiện rvt-mcp. Cảm ơn:

| Người đóng góp | Đóng góp |
|----------------|----------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | Báo lỗi kèm ví dụ tái hiện về thành phần mạng MEP và xử lý hệ thống ống ([#11](https://github.com/bimwright/rvt-mcp/issues/11), [#12](https://github.com/bimwright/rvt-mcp/issues/12)). |
| [@razmikb](https://github.com/razmikb) | Báo lỗi đặt family có host và xử lý lỗi cầu thang/send-code, giúp bổ sung kiểm tra vị trí, hỗ trợ lớp helper và mở rộng kiểm thử xử lý lỗi ([#13](https://github.com/bimwright/rvt-mcp/issues/13), [#14](https://github.com/bimwright/rvt-mcp/issues/14)). |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | Đề xuất Family Authoring Tool Suite giúp làm rõ lộ trình và phạm vi sản phẩm ([#7](https://github.com/bimwright/rvt-mcp/issues/7)). |
| [@PhanCongVuDuc](https://github.com/PhanCongVuDuc) | Pull request [#15](https://github.com/bimwright/rvt-mcp/pull/15): bản sửa để `send_code` chạy được khi chưa mở model, sửa gợi ý tham số của `revit_switch_target`, và đề xuất `revit_open_model` (được làm lại rồi phát hành trong v0.8.1). |

---

## bimwright

Các công cụ mã nguồn mở kết nối trợ lý AI với ứng dụng BIM và CAD.

Tên **bimwright** ghép **BIM** với **wright**, một từ tiếng Anh cổ chỉ người thợ chế tạo hoặc xây dựng — như trong *shipwright* (thợ đóng tàu).

Xem [cách đặt tên các gateway](https://github.com/bimwright/.github/blob/master/profile/README.vi.md#cách-đặt-tên).

- [rvt-mcp](https://github.com/bimwright/rvt-mcp) — Revit  
- [dwg-mcp](https://github.com/bimwright/dwg-mcp) — AutoCAD  
- [nwd-mcp](https://github.com/bimwright/nwd-mcp) — Navisworks  
- [ipt-mcp](https://github.com/bimwright/ipt-mcp) — Inventor  
- [bim-wiki](https://github.com/bimwright/bim-wiki) — Kho BIM ưu tiên tiếng Việt  

---

## License

Apache-2.0 — [LICENSE](LICENSE).

Bạn cứ tự do fork và đổi thương hiệu — chỉ cần tuân thủ điều khoản license (giữ `LICENSE` và các copyright notice, ghi chú file đã sửa). Nếu rvt-mcp có ích cho bạn, một star hoặc lời nhắc tới BIMwright trong sản phẩm của bạn là điều tác giả rất trân trọng, nhưng hoàn toàn tùy ý. Luôn chào đón issue và PR.

Revit và Autodesk là nhãn hiệu của Autodesk, Inc. bimwright là dự án mã nguồn mở độc lập, không liên kết, không được tài trợ hay chứng thực bởi Autodesk, Inc.
