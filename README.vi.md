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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-233%20tools-6C47FF" alt="MCP tools" /></a>
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

**Để AI agent cài cho client bạn muốn sử dụng.** Dùng agent có thể chạy PowerShell và sửa file trên máy Windows này (Claude Code, Codex, Cursor, …), không phải phiên chat thiếu công cụ thao tác máy. Agent thực hiện cài đặt có thể khác client bạn dùng để làm việc với Revit. Agent làm theo [AGENTS.md](AGENTS.md), xem trước thay đổi và hỏi trước khi áp dụng; bạn vẫn có thể cần thao tác UI hoặc khởi động lại ứng dụng.

Với **Claude Desktop**, dán:

```text
Hãy cài rvt-mcp để tôi sử dụng trong Claude Desktop trên máy Windows này.
Đọc https://github.com/bimwright/rvt-mcp/blob/master/AGENTS.md trước.
Chỉ cấu hình Claude Desktop, không cấu hình client khác. Kiểm tra cài đặt hiện có,
xem trước thay đổi và xin xác nhận trước khi ghi. Nếu cần tôi thao tác UI
hoặc restart ứng dụng, hãy nói rõ.
```

Với client khác, thay **Claude Desktop** bằng tên client đó. **Claude Code và Claude Desktop là hai đích khác nhau:** tùy chọn installer `claude` là Code; `claude-desktop` là Desktop.

**Hoặc tự chạy installer (Claude Desktop, config trực tiếp).** Đóng Revit và thoát hẳn Claude Desktop (kể cả tiến trình ở khay hệ thống), rồi trong PowerShell:

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile "$dir.zip"
Expand-Archive "$dir.zip" -DestinationPath $dir -Force
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf -Client claude-desktop
# Đọc bản xem trước rồi mới áp dụng:
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client claude-desktop
```

Lệnh này cài add-in cho mọi bản Revit 2022–2027 phát hiện được và chỉ đăng ký `rvt-mcp` trong Claude Desktop (config được backup trước). Với Claude Code dùng `-Client claude`; với nhiều client được yêu cầu dùng danh sách phân cách bằng dấu phẩy. Bỏ `-Client` sẽ nối mọi client phát hiện được; `-Client none` giữ nguyên config. Server nằm ở `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe` ([các bước cho từng client, gồm đường dẫn Desktop classic/MSIX](docs/mcp-client-wiring.md)).

**Kiểm tra:** khởi động lại AI client, mở một model trong Revit, bật MCP trên ribbon (**Add-Ins** → **RvtMcp**) rồi nhờ agent gọi `revit_get_current_view_info`. Kết quả phải là tên và loại của view đang mở.

**Cập nhật:** đóng Revit và các client dùng gateway, backup thư mục dữ liệu rồi chạy installer mới để nâng cấp tại chỗ, không gỡ trước. Cài đặt và dữ liệu ToolBaker được giữ. User dùng MCPB chọn `-Client none`, cập nhật Setup và extension cùng release, rồi kiểm tra settings và quyền trong Desktop trước khi kết nối lại. Xem [trình tự nâng cấp](docs/install.md#upgrade). **Gỡ cài:** `uninstall.ps1 -Yes` gỡ add-in và server; dữ liệu cá nhân được giữ trừ khi thêm `-Purge`. Muốn xóa cả entry trong client, chạy `install.ps1 -Uninstall -Client auto` trước.

**Nâng cấp từ v0.8.1 hoặc v0.6.x:** các bản này lưu cài đặt và dữ liệu ToolBaker trong `%LOCALAPPDATA%\RvtMcp\`; v1.0.0 dùng `%LOCALAPPDATA%\Bimwright\rvt-mcp\` và installer sẽ chuyển folder cũ sang đó. Hãy đóng Revit và mọi MCP client trước, vì server cũ đang chạy sẽ khóa folder. Kiểm tra trong PowerShell bằng `Test-Path "$env:LOCALAPPDATA\RvtMcp"`. Nếu installer dừng với thông báo `Both … exist` hoặc `Could not move …`, nó đã hoàn tác phần đã di chuyển: giữ folder đang chứa cài đặt và dữ liệu ToolBaker của bạn, **đổi tên folder còn lại (ví dụ thành `RvtMcp.bak`) thay vì xóa**, đóng các client đang dùng nó rồi chạy lại installer.

**Extension Claude Desktop (MCPB, tùy chọn):** config trực tiếp ở trên là đường mặc định khi agent cài hộ. Nếu muốn UI extension/settings, chạy Setup với `-Client none`, rồi cài `.mcpb` qua UI extension của Desktop. **Chọn một đường, không làm cả hai.** `-Client none` không xóa registration thủ công đã có. Dùng bundle từ cùng release đã chọn với Setup; extension không cài gateway/add-in và yêu cầu đúng server build của release đó. Kiểm tra release notes về trạng thái ký. Xem [cài MCPB và các setting](docs/install.md#claude-desktop-mcpb).

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
| Fresh install | **233** | `install.ps1` seeds `"toolsets": ["all"]` trong `rvtmcp.config.json` |
| Bare `rvt-mcp.exe` | **46** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **233** | Full catalog |
| `all` + adaptive bake | **236** | Thêm 3 tool vòng đời suggestion |

Số lượng chưa tính baked tool cá nhân. Installer chỉ seed khi `rvtmcp.config.json` chưa có `toolsets` — list của bạn sống sót qua upgrade; bỏ key (hoặc đặt CSV riêng) thì bare server về mặt 46 tool. Read-only lọc từng tool theo `ReadOnly=true`, nên vẫn giữ công cụ đọc trong nhóm hỗn hợp. Tool có thể ghi file sẽ bị loại, kể cả khi output mặc định là inline.

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
| `sheets` | Sheet, titleblock, revision, renumber, bố cục viewport |
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

Hover có chủ ý mở timeline với ba kết quả gần nhất và giờ hoàn tất local (`HH:mm:ss`). Cuộn lên để xem các lần gọi trước trong cùng card. Khi ở cuối, kết quả mới trượt vào ngắn (các dòng đẩy lên, dòng mới hiện dần, chấm kết quả nảy lên; tắt animation thì hiện thẳng); khi đọc phía trên, vị trí được giữ nguyên. Bấm trong timeline để đọc mà không đóng card; bấm phần còn lại mở History. Tính năng hoạt động cả khi branding tắt. Tóm tắt được giới hạn và che thông tin nhạy cảm, chỉ giữ trong RAM và xóa khi card đóng. Object/array của script hiện số lượng; khảo sát chưa đủ vẫn ghi rõ chưa đủ. Thay đổi UI này không bổ sung toast cho tool chạy thuần server.

Toast **bật mặc định** và có thể tắt. Trong **Settings → Toast**, chọn thời gian tự ẩn (10/20/30/60 giây; mặc định 20) và **Show branding** (**tắt mặc định**); bật branding sẽ hiện wordmark khi hover. Lựa chọn có hiệu lực ngay và được lưu qua các lần khởi động Revit; không cần bật brand để nhận thông báo hoạt động.

**Vị trí:** trong **Settings → Toast**, chọn riêng **Horizontal alignment** (Trái/Phải) và **Vertical alignment** (Trên/Dưới); mặc định là trên-trái. Thay đổi áp dụng ngay cho card đang hiện và được lưu ngay; đổi góc sẽ xóa vị trí kéo đã lưu. Bật **Allow dragging the card** để kéo card bằng hàng tiêu đề; bấm thường vẫn mở History. Tắt kéo thì giữ vị trí đã lưu, bật lại sẽ khôi phục, còn **Reset position** xóa vị trí đó. Vị trí được lưu tương đối so với cửa sổ Revit và luôn nằm trong vùng làm việc của màn hình. Card neo ở dưới sẽ mở rộng lên trên, và chuyển sang phía còn lại khi không đủ chỗ. Chuyển động theo tùy chọn animation của Windows. Lưu thất bại sẽ báo ngay dưới setting, lựa chọn vẫn có hiệu lực trong phiên. Rê chuột lên dòng tool mới nhất hoặc một dòng timeline sẽ hiện kết quả đã lọc thông tin nhạy cảm, Success/Failed, giờ hoàn tất và thời lượng đo được (nếu có). Việc phối hợp toast giữa nhiều ứng dụng Autodesk không thuộc phạm vi này.

### Prompt

v1.0.0 có sáu MCP prompt — chọn `/mcp__rvt-mcp__revit_<name>` (Claude Code) hoặc menu prompt (Claude Desktop); agent làm theo kịch bản bằng các tool sẵn có:

- `revit_getting_started` — làm quen với model đang mở (chỉ đọc, chạy được với cấu hình mặc định).
- `revit_drawing_layout` — truyền `request`: sắp xếp viewport của một sheet. Đọc vị trí thật trong không gian sheet trước (`revit_get_viewport_geometry`), thống nhất cách căn với bạn, hiện kế hoạch chạy thử (`revit_align_viewports`), chỉ di chuyển sau khi bạn xác nhận, rồi đọc lại. Cần `query,sheets,view,meta`. Chế độ read-only chỉ báo cáo và đề xuất.
- `revit_change` — truyền `change`: khảo sát quan hệ, chốt phạm vi nhỏ nhất cho từng yêu cầu, xác nhận phương án cụ thể trước khi ghi, rồi đọc lại và ghi lý do trong hội thoại. Cần `query,meta`, không cần `send_code`; chế độ read-only dừng ở khảo sát/đề xuất. Dữ liệu thiếu hoặc chưa đầy đủ phải ghi "không kiểm được". Đọc lại không phải danh sách đầy đủ mọi phần tử bị đổi gián tiếp; bản ghi trong hội thoại không phải cơ sở dữ liệu lưu bền. Prompt hướng dẫn agent, không phải khóa workflow do server cưỡng chế.
- `revit_model_audit` — kiểm tra sức khỏe model: cảnh báo, family, ứng viên purge ở chế độ chạy thử (cần `workflows,families,lint,meta`).
- `revit_pre_issue_check` — kiểm tra các sheet đã xác định trước khi phát hành (cần `sheets,view,annotation,lint,meta`). Truyền số/ID sheet, bộ lọc số/tên rõ ràng, hoặc `all`; sheet set có tên cần các sheet thành viên. Cảnh báo model chỉ lấy mẫu và kiểm tra chưa đầy đủ được báo là **NOT VERIFIED**, không phải đạt ở mức sheet.
- `revit_stairs` — tạo thang có hướng dẫn qua `send_code` (chỉ ghi sau khi bạn xác nhận). Gồm mẫu transaction/xử lý lỗi/dọn dẹp; không cần source checkout.

Nếu toolset mà prompt cần chưa bật, prompt trả lời đúng dòng `--toolsets` cần thêm — không có gì chạy khi cấu hình dở dang. Bảo vệ read-only vẫn bật khi các tool còn thiếu cho phép; prompt cần toolset có quyền ghi sẽ giải thích xung đột thay vì âm thầm đổi cấu hình. Prompt là hướng dẫn cho agent, không phải khóa quy trình do server cưỡng chế.

---

## Cấu hình

Từ v1.0.0, server trả `_changes` và `_history` theo model. Lịch sử cục bộ mặc định bật, độc lập với call log; dùng `--disable-change-history` để tắt ghi. Ba tool `meta`: `revit_record_change` gắn lý do với nhóm call ID cụ thể, `revit_get_change_records` tra cứu thay đổi, `revit_resolve_history_identity` ghi lựa chọn tiếp nối hoặc tách lịch sử sau copy/Save As. Xem [ghi nhận thay đổi](docs/change-tracking.md) về riêng tư, giới hạn và phục hồi.

`revit_survey_change_impact` là tool trong `query`: khảo sát chỉ đọc 10 nhóm quan hệ, dùng được khi tắt send-code. Mỗi yêu cầu cần `scopeThreshold`; chỉ quét view/schedule khi `maxViews > 0`. Kết quả thiếu phải hiện rõ; giá trị quan sát chưa phải snapshot tin cậy cho history. Xem [khảo sát ảnh hưởng](docs/change-survey.md).

Ưu tiên, cao thắng: **CLI → env (`BIMWRIGHT_*`) →** `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json`.

| Setting | CLI | Env | JSON |
|---------|-----|-----|------|
| Selector target | `--target 2024` / `--target pid:12345` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| send_code | `--enable-send-code` / `--disable-send-code` | `BIMWRIGHT_ENABLE_SEND_CODE` | `enableSendCode` |
| Call log | `--enable-call-log` / `--disable-call-log` | `BIMWRIGHT_ENABLE_CALL_LOG` | `enableCallLog` |
| Change history (default ON) | `--enable-change-history` / `--disable-change-history` | `BIMWRIGHT_ENABLE_CHANGE_HISTORY` | `enableChangeHistory` |
| Response guard | `--enable-response-guard` / `--disable-response-guard` | `BIMWRIGHT_ENABLE_RESPONSE_GUARD` | `enableResponseGuard` |
| Warn bytes | `--response-warn-bytes` | `BIMWRIGHT_RESPONSE_WARN_BYTES` | `responseWarnBytes` |
| Strong warn bytes | `--response-strong-warn-bytes` | `BIMWRIGHT_RESPONSE_STRONG_WARN_BYTES` | `responseStrongWarnBytes` |
| Budget bytes | `--response-budget-bytes` | `BIMWRIGHT_RESPONSE_BUDGET_BYTES` | `responseBudgetBytes` |
| Transport cap | `--max-response-bytes` | `BIMWRIGHT_MAX_RESPONSE_BYTES` | `maxResponseBytes` |
| Thời gian giữ file spill (mặc định 36 giờ) | `--spill-retention-hours <n>` | `BIMWRIGHT_SPILL_RETENTION_HOURS` | `spillRetentionHours` |
| LAN bind (plugin) | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker surface | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache body send_code (cluster bake) | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| Journal persist send_code | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| TTL journal | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| Toast hoàn thành (mặc định bật) | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| Brand trên toast (mặc định tắt, có lưu) | Settings → Toast → **Show branding** | — | `showBranding` |
| Thời gian toast tự ẩn (mặc định 20 giây) | Settings → Toast → **Idle duration** | — | `toastIdleSeconds` |
| Vị trí toast (mặc định trên-trái, không kéo; có lưu) | Settings → Toast → **Horizontal / Vertical alignment**, **Allow dragging the card**, **Reset position** | — | `toastHorizontalAlign`, `toastVerticalAlign`, `toastDragEnabled`, `toastDragOffset` |
| Ngôn ngữ UI (add-in) | ribbon **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

Đổi cờ server xong: restart kết nối MCP để client nhận tool list mới.

---

## Permissions & auto mode — quyền chạy tự động

Các controls này có từ v1.0.0. Gói v0.8.1 chưa có các switch mới và cách lọc read-only theo từng tool. Phải gọi `send_code` và `run_baked_tool` trực tiếp; `batch_execute` từ chối hai lệnh này.

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
      "mcp__rvt-mcp__revit_get_change_records",
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
      "mcp__rvt-mcp__revit_get_viewport_geometry",
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
      "mcp__rvt-mcp__revit_survey_change_impact",
      "mcp__rvt-mcp__revit_workflow_model_audit"
    ]
  }
}
```
<!-- END READ_ONLY_ALLOWLIST -->

</details>

send_code mặc định **bật**, độc lập với ToolBaker; call-log mặc định **tắt**. CLI ưu tiên hơn environment, rồi đến JSON. Cấu hình server đã xác thực được truyền sang plugin riêng cho từng request. Khi call-log tắt, server journal, plugin `mcp-calls.jsonl` và journal body send-code đều không ghi; History trong bộ nhớ vẫn hoạt động. Journal body cần đồng thời bật call-log và opt-in TTL riêng. `usage.jsonl` của ToolBaker là luồng riêng, theo cấu hình adaptive-bake.

Response guard mặc định **bật**: cảnh báo tại 65536 byte UTF-8, cảnh báo mạnh trên 262144, budget 716800 và transport cap 1048576 byte. Server đo cả JSON escaping, content và metadata MCP. Kết quả đọc quá lớn trả `RESPONSE_TOO_LARGE` cùng cách thu hẹp; lệnh ghi đã hoàn tất trả tóm tắt. Output code tùy ý được spill ra file cục bộ với `mutation_applied: null`; đọc file đó, không chạy lại lệnh. File spill được giữ 36 giờ theo mặc định (`--spill-retention-hours`, 1-8760; giá trị không hợp lệ dùng 36) và không có giới hạn số file nào xóa file còn trẻ hơn. Tắt guard vẫn giữ transport cap. Các ngưỡng phải là số nguyên >=1024, theo `warn <= strong <= budget <= max`; khi giảm budget, giảm các ngưỡng cảnh báo tương ứng.

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Chỉ Revit desktop đầy đủ; Revit Viewer không được hỗ trợ. CI build cả 6 add-in, nhưng độ sâu runtime khác nhau theo năm — kiểm lại baked tool và C# custom trên các năm bạn dùng.

---

## Nhiều phiên bản Revit chạy đồng thời

Server chỉ bind vào đúng một phiên bản Revit — kể cả khi có nhiều phiên bản cùng năm chạy song song. Nó bind ngầm ở lệnh gọi đầu tiên hoặc tường minh qua `--target` / `BIMWRIGHT_TARGET` / `revit_switch_target`. Selector: `auto`, năm 4 chữ số (`2024`), `pid:<n>` hoặc `id:revit-<year>-<pid>`; các mã R cũ như `R24` bị từ chối.

Khi có nhiều phiên bản chạy — nhất là nhiều phiên bản cùng năm — đừng đoán target:

1. Gọi `revit_list_available_targets`: mọi phiên bản đang sống được liệt kê kèm `target_id`, `pid`, `host_year`, `window_title` và `source`.
2. Chọn phiên bản theo `pid` và/hoặc `window_title`.
3. Ghim bằng `revit_switch_target("pid:<n>")` — mọi dạng selector đều dùng được, `pid:` là rõ ràng nhất.
4. `revit_get_current_target` hiển thị binding hiện tại, generation và `next_target_id` được đề xuất.

Mã lỗi target và cách xử lý. Mọi mã trừ `TARGET_INTERRUPTED` nghĩa là lệnh **chưa** được gửi:

| Mã | Ý nghĩa | Hành động của agent |
|---|---|---|
| `NO_TARGET` | Không có phiên bản sống nào khớp selector (cũng xảy ra khi implicit binding mất và còn 0 candidate) | Gọi `revit_list_available_targets`; mở Revit / add-in hoặc sửa selector |
| `TARGET_UNAVAILABLE` | Phiên bản đang bind vẫn sống nhưng không kết nối được — listener đã dừng/đang khởi động lại, descriptor bị thiếu, token bị từ chối. Binding được giữ nguyên | Thử lại sau ít lâu; nhờ người dùng (khởi động lại) MCP trong Revit đó. Đừng đổi sang phiên bản khác trừ khi người dùng muốn |
| `TARGET_BUSY` | Endpoint tồn tại nhưng handshake không hoàn tất trong 5 s — thường do agent/gateway khác đang giữ kết nối duy nhất, hoặc Revit đang bị chặn | Thử lại sau; đừng chạy hai gateway trên một phiên bản |
| `TARGET_LOST` | Phiên bản được ghim tường minh (`pid:`/`id:`) đã thoát | Xem `candidates`, xác nhận với người dùng, rồi `revit_switch_target` |
| `TARGET_CHANGED` | Phiên bản được bind ngầm đã thoát và có đúng một phiên bản thay thế (hoặc binding đổi giữa lúc nhận và gửi lệnh). Lệnh **chưa** được gửi; mọi call đều trả mã này cho tới khi được xác nhận | Kiểm tra phiên bản được đề xuất (`window_title`), xác nhận bằng `revit_switch_target`, rồi gọi lại |
| `AMBIGUOUS_TARGET` | Phiên bản được bind ngầm đã thoát và ≥2 phiên bản khớp | Liệt kê, chọn theo `pid`/`window_title`, `revit_switch_target("pid:<n>")` |
| `TARGET_INTERRUPTED` | Lệnh đã được gửi nhưng kết nối đóng trước khi có phản hồi (`sent:true`, `outcome:unknown`) | Kiểm tra trạng thái model trước khi lặp lại bất kỳ thao tác ghi nào |
| `INVALID_TARGET_SELECTOR` | Selector sai định dạng hoặc mã R | Dùng `auto`, năm, `pid:<n>` hoặc `id:<target_id>` |

**Sau khi khởi động lại Revit — kể cả khi chỉ mở một Revit** — lời gọi tiếp theo trả về `TARGET_CHANGED` và server chờ một lần gọi `revit_switch_target` rồi mới gửi lệnh. Đây là chủ đích: Revit vừa khởi động lại có thể đang mở một model khác, nên server không bao giờ tự bind lại một cách âm thầm. Payload có ghi instance được đề xuất; hãy xác nhận rồi gọi lại lệnh.

`revit_switch_target` được chú thích non-read-only vì nó đổi định tuyến của phiên, nhưng vẫn khả dụng dưới `--read-only` để luôn khôi phục được binding.

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

## Họ bimwright

Các công cụ mã nguồn mở kết nối trợ lý AI với ứng dụng BIM và CAD.

Tên **bimwright** ghép **BIM** với **wright**, một từ tiếng Anh cổ chỉ người thợ chế tạo hoặc xây dựng — như trong *shipwright* (thợ đóng tàu).

Xem [cách đặt tên các gateway](https://github.com/bimwright/.github/blob/master/profile/README.vi.md#cách-đặt-tên).

- [**rvt-mcp**](https://github.com/bimwright/rvt-mcp) — Autodesk® Revit®
- [**dwg-mcp**](https://github.com/bimwright/dwg-mcp) — Autodesk® AutoCAD®
- [**nwd-mcp**](https://github.com/bimwright/nwd-mcp) — Autodesk® Navisworks®
- [**ipt-mcp**](https://github.com/bimwright/ipt-mcp) — Autodesk® Inventor®
- [**bim-wiki**](https://github.com/bimwright/bim-wiki) — Kho kiến thức BIM ưu tiên tiếng Việt

---

## License

Apache-2.0 — [LICENSE](LICENSE).

Bạn cứ tự do fork và đổi thương hiệu — chỉ cần tuân thủ điều khoản license (giữ `LICENSE` và các copyright notice, ghi chú file đã sửa). Nếu rvt-mcp có ích cho bạn, một star hoặc lời nhắc tới BIMwright trong sản phẩm của bạn là điều tác giả rất trân trọng, nhưng hoàn toàn tùy ý. Luôn chào đón issue và PR.

Revit và Autodesk là nhãn hiệu của Autodesk, Inc. bimwright là dự án mã nguồn mở độc lập, không liên kết, không được tài trợ hay chứng thực bởi Autodesk, Inc.
