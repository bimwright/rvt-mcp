# Brief: Xử lý response >1 MiB — khảo sát tool + lộ trình 2 bước

> **Trạng thái:** COMPLETE — Bước 1 + Bước 2 hoàn tất; Chốt Dừng #3 đã được User duyệt.
> **Cơ chế kiểm soát:** User (Khoa) + Claude giám sát. Có **3 chốt dừng bắt buộc** (xem §7).
> **Repo:** `rvt-mcp` (chỉ repo này). Nhánh làm việc hiện tại: `hardening/agent-guardrails` (PR #10).
> **Lưu ý lịch sử:** các count 227/230 và 468 test bên dưới là snapshot khi brief hoàn tất; count hiện hành được khóa bởi `tests/RvtMcp.Tests/Golden/tools-list*.json`.

---

## 0. TL;DR cho agent nhận việc

Bạn được giao **2 giai đoạn, tách bạch**:

1. **KHẢO SÁT (làm trước, KHÔNG sửa code):** rà toàn bộ ~227 tool MCP, phân loại tool nào có thể trả về payload >1 MiB, xuất ra một **bảng markdown**. Dừng lại, chờ duyệt.
2. **THI CÔNG (chỉ sau khi bảng được duyệt):** sửa theo 2 bước dưới đây, dựa trên bảng khảo sát.

**Tuyệt đối không** nhảy vào sửa code trong lúc khảo sát. **Tuyệt đối không** gộp 2 giai đoạn.

---

## 1. Bối cảnh (đọc để hiểu, không được bỏ qua)

- `rvt-mcp` là MCP gateway điều khiển Autodesk Revit. Kiến trúc: MCP Server (.NET 8, `src/server/`) ⇄ NDJSON qua TCP/Named Pipe ⇄ plugin trong Revit (`src/shared/` + `src/plugin-rXX/`).
- **Tool được định nghĩa tập trung** trong `src/server/Program.cs` (~256 khai báo `[McpServerTool]`, tương ứng ~227 tool). Mỗi tool là một static method có `[McpServerTool]` + mô tả tham số.
- **Logic thực thi** nằm ở handler: `src/shared/Handlers/*.cs` (228 file). Handler trả về **DTO (anonymous object / JObject)** — đây là nơi quyết định kích thước payload.
- **Guard kích thước** hiện có: `src/shared/Infrastructure/ResponseSizeGuard.cs`
  - `DefaultThresholdBytes = 100 KB` → **cảnh báo** (warn).
  - `MaxResponseBytes = 1 MiB` → **chặn** (reject): thay payload bằng `{ success=false, error=<hướng dẫn thu hẹp> }`.
  - Enforcement ở `src/shared/Infrastructure/McpEventHandler.cs` (~dòng 202–216): gọi `ResponseSizeGuard.Evaluate(...)`, nếu `Reject` thì đóng gói lại thành lỗi.
- **PR #10 (`hardening/agent-guardrails`)** vừa biến guard này từ "chỉ cảnh báo" thành "chặn cứng >1 MiB". Brief này là bước tiếp theo để cơ chế chặn đó *thông minh* hơn, không làm hỏng trải nghiệm.

### Vì sao phải chặn >1 MiB
Payload quá lớn (1) làm **tràn context window** của client (nhồi vài MiB text là agent "mù"), và (2) **nghẽn đường truyền** pipe/TCP. Chặn là để một cú dump dữ liệu khổng lồ không làm sập cả phiên. Đây là hành vi **cố ý**, không phải bug.

---

## 2. Vấn đề cần giải

Chặn cứng có 2 tác dụng phụ:
1. **Tool đọc không có cơ chế thu hẹp** (vd cây model, thống kê toàn công trình) → agent bị kẹt, không lấy được dữ liệu.
2. **Tool ghi/sửa** mà response >1 MiB → thay đổi *đã áp dụng* trong Revit rồi, nhưng agent thấy `success=false` → có thể retry → **sửa 2 lần** (nguy hiểm).

Mục tiêu: **tool phải giúp agent hoàn thành việc user giao NHANH**, đồng thời **đáng tin** và **đúng kiến trúc**.

### Ràng buộc kiến trúc (BẤT KHẢ XÂM PHẠM)
- **Client-agnostic:** MCP server không được giả định client nào cũng chạy được Python/SQL. Claude Code chạy được; Cursor/Cline/client khác thì không chắc. → **Không** bundle script Python như một dependency *bắt buộc*.
- **DTO mapping vẫn bắt buộc:** không serialize object Revit trực tiếp.
- **Đơn vị I/O vẫn là mm**, convert ở biên handler.

---

## 3. Giải pháp đã chốt: phân tầng (KHÔNG chọn thuần A hay thuần B)

| Ngưỡng | Hành vi |
|---|---|
| < 100 KB | Trả thẳng (giữ nguyên) |
| 100 KB – 1 MiB | Trả thẳng + cảnh báo (giữ nguyên) |
| > 1 MiB | **Bước 1** mặc định: chặn + hướng dẫn thu hẹp *cụ thể*. **Bước 2**: một số tool bulk được phép spill ra file thay vì chặn. |

- **A = spill ra file + agent tự query** → nhanh cho task phân tích toàn model, nhưng phức tạp + rò rỉ nếu áp cho *tất cả* tool. → chỉ dùng cho **thiểu số** tool bulk (escape hatch).
- **B = chặn + bảo thu hẹp** → an toàn, đơn giản, đúng cho **đa số** tool. → làm mặc định, nhưng phải làm **cho tử tế** (thông báo lỗi trỏ đúng tham số).

Quyết định kỹ thuật kèm theo (áp dụng khi làm Bước 2):
- **CSV bị loại** — dữ liệu Revit phần lớn nested (cây model, params lồng nhau), CSV chỉ hợp bảng phẳng.
- **JSON file "đọc cả file vào context" bị loại** — không giải quyết vấn đề context. File chỉ có giá trị nếu agent *query* được nó.
- **SQLite** = lựa chọn cho tool bulk có **cấu trúc bảng rõ ràng** (agent chạy SQL, chỉ kéo phần cần).
- **NDJSON / JSON / text thô** = cho output **tự do / bất định** (đặc biệt `send_code`).
- Khi spill: trả `path` + kích thước thật + **schema/format mô tả trong response** + **preview** (vd 50 KB đầu). Để agent tự dùng công cụ của nó (python/jq) mà phân tích.

---

## 4. GIAI ĐOẠN 1 — KHẢO SÁT (deliverable đầu tiên, KHÔNG sửa code)

Rà **toàn bộ** tool. Với mỗi tool, đọc: (a) khai báo `[McpServerTool]` trong `src/server/Program.cs`, (b) handler tương ứng trong `src/shared/Handlers/*.cs` để xem DTO trả về + có tham số thu hẹp không.

Phân mỗi tool vào **1 trong 4 nhóm**:

| Nhóm | Định nghĩa | Hành động dự kiến |
|---|---|---|
| **1 — An toàn** | Output có trần nhỏ cố định (vd `create_grid`, `get_current_target`) | Không đụng |
| **2 — Rủi ro, ĐÃ có scope** | Có thể >1 MiB, nhưng đã có `max_results`/filter/pagination/id | Bước 1: chỉ nâng thông báo lỗi trỏ đúng tham số |
| **3 — Rủi ro, THIẾU scope** | Có thể >1 MiB mà không có đường thu hẹp | Bước 1: **bổ sung** tham số scope |
| **4 — Bulk / bất định** | Bản chất cần full dataset, hoặc output không đoán trước | Bước 2: opt-in spill file |
| **(riêng)** | `send_code` | Xử lý riêng — xem §6 |

### Định dạng bảng khảo sát (xuất ra `docs/design/oversized-response-survey.md`)

```
| Tool (revit_*) | Toolset | Handler file | Ước lượng size rủi ro | Đã có scope? (tham số nào) | Nhóm | Đề xuất hành động |
```

- "Ước lượng size rủi ro": Low / Medium / High + 1 câu lý do (vd "trả list mọi element, không giới hạn").
- Cuối bảng: **tóm tắt đếm** mỗi nhóm + **danh sách ứng viên Bước 2** (nhóm 4).

**CHỐT DỪNG #1:** nộp bảng, chờ User + Claude duyệt & chỉnh phân loại. **Không sang §5 nếu chưa được duyệt.**

---

## 5. GIAI ĐOẠN 2 — THI CÔNG

### Bước 1 — làm cho phần chặn "tử tế" (nhóm 2 & 3)
Theo TDD (viết test trước; repo dùng `tests/RvtMcp.Tests/`).

1. **Thông báo lỗi cụ thể theo tool:** khi reject, message phải nêu **đúng tham số thu hẹp của chính tool đó** (vd `get_model_tree` → "dùng `root_id` + `depth`"), không dùng câu chung chung. Cân nhắc cơ chế cho phép handler/tool khai báo "gợi ý thu hẹp" của riêng nó, thay vì hardcode trong `ResponseSizeGuard`.
2. **Bổ sung scope cho nhóm 3:** thêm `max_results`/filter/pagination cho các tool thiếu. Mỗi tool = một thay đổi nhỏ, có test.
3. **Xử lý tool GHI (quan trọng):** với command **mutation**, response >1 MiB **không được** biến thành `success=false` gây hiểu lầm "thất bại" (vì thay đổi đã áp dụng). Phương án: mutation trả về **payload tóm tắt gọn** (id + count), không bao giờ dump toàn bộ; nếu vẫn to thì truncate phần data nhưng **giữ `success=true`**. Làm rõ trong test.

**CHỐT DỪNG #2:** review Bước 1 (Claude + User) trước khi sang Bước 2.

### Bước 2 — escape hatch spill file (nhóm 4)
1. Thêm tham số **opt-in** `output=file` (mặc định vẫn inline/chặn) cho các tool nhóm 4 đã duyệt.
2. Định dạng: **SQLite** nếu dữ liệu có bảng rõ ràng; **NDJSON/JSON** nếu nested/tự do.
3. Response trả: `path`, `byte_count`, `format`, `schema` (mô tả cột/khoá), `preview` (vd 50 KB đầu).
4. **Vòng đời file:** quyết định thư mục (gợi ý dưới `%LOCALAPPDATA%\RvtMcp\spill\`), cơ chế dọn (vd xoá file cũ theo tuổi/định mức), tránh 2 phiên ghi đè nhau (đặt tên theo pid + timestamp + command).
5. **Không** bundle Python bắt buộc. Nếu muốn tiện, chỉ kèm một helper mỏng "mở file + in schema" và mô tả rõ trong response — agent tự sinh query.

---

## 6. `send_code` — XỬ LÝ ĐẶC BIỆT (điểm hiểm nhất)

**Vì sao khác mọi tool:** Bước 1 dựa trên việc *thêm tham số thu hẹp vào tool*. Nhưng `send_code` để **agent tự viết code C#** chạy trong Revit, trả về **bất kỳ thứ gì** — không schema, không tham số nào để "thêm scope". Bạn không kiểm soát *input*, chỉ phản ứng ở *output*.

**Chặn cứng `send_code` là SAI** — nó chính là escape hatch cho việc bulk; chặn thì mất tác dụng.

**Yêu cầu cho `send_code`:**
1. Kiểm tra kích thước output **tại boundary**. Nếu >1 MiB (ngưỡng có thể cấu hình):
2. **Tự động** (không opt-in) spill output ra file — định dạng **NDJSON/JSON hoặc text thô** (vì output tự do, **không** ép SQLite schema).
3. Trả về: `path` + `byte_count` + **preview** (vd 50 KB đầu, để agent biết dữ liệu trông thế nào) + hướng dẫn "dữ liệu lớn đã lưu ra file, hãy dùng công cụ của bạn để phân tích".
4. Handler liên quan: tìm trong `src/shared/Handlers/` (vd `SendCodeToRevitHandler.cs` hoặc tương tự) + đường enforcement ở `McpEventHandler.cs`.

Ghi chú: cân nhắc để cùng một **cơ chế spill** dùng chung cho §5-Bước 2 và §6, chỉ khác ở chỗ `send_code` là auto-theo-size còn tool nhóm 4 là opt-in.

---

## 7. Chốt kiểm soát & nguyên tắc thực thi

- **CHỐT DỪNG #1:** sau khi có bảng khảo sát (§4) → chờ duyệt.
- **CHỐT DỪNG #2:** sau Bước 1 (§5) → review trước khi sang Bước 2.
- **CHỐT DỪNG #3:** sau Bước 2 + `send_code` → review tổng thể trước khi coi là xong.
- **TDD bắt buộc** cho mọi thay đổi code (test đỏ → xanh → refactor). Dùng `tests/RvtMcp.Tests/`.
- **Surgical:** mỗi dòng đổi phải truy về được yêu cầu này. Không refactor lan man.
- **Build:** Revit phải ĐÓNG trước khi build (plugin DLL bị khoá). Server luôn build/test được không cần Revit.
- **Không** đụng repo khác (dwg/nwd/ipt). Chỉ `rvt-mcp`.
- Cập nhật `CLAUDE.md` (mục "Threading" ghi "reject above 1 MiB") nếu hành vi đổi.

## 8. Tiêu chí nghiệm thu (Definition of Done)

- [x] Bảng khảo sát đầy đủ 230 tool, phân 4 nhóm, được duyệt.
- [x] Nhóm 2: thông báo lỗi reject trỏ đúng tham số thu hẹp của từng tool (có test).
- [x] Nhóm 3: đã bổ sung scope, không còn tool đọc "kẹt cứng" khi model lớn (có test).
- [x] Tool ghi: response lớn không còn giả `success=false` (có test).
- [x] Nhóm 4: `output=file` opt-in hoạt động, có schema + preview + dọn file (có test).
- [x] `send_code`: auto spill khi output lớn, trả path + preview (có test).
- [x] Toàn bộ 468 test xanh; golden snapshot (`tests/RvtMcp.Tests/Golden/`) đã cập nhật.
- [x] Không hồi quy hành vi tool dưới enforcement budget.

---

# PHỤ LỤC — SPEC BƯỚC 2 (đã triển khai)

> **Tiền đề:** Bước 1 đã hoàn tất và merge-ready (guard 3 mức 64/256 KiB + budget 700 KiB, scope nhóm 3, mutation-preserve, 424 test xanh). Bước 2 chỉ đụng 8 tool nhóm 4 + `send_code`; **không** sửa lại nhóm 1/2/3.

## B2.0 — Quyết định đã khóa (User chốt)

1. **Phạm vi filesystem: LOCAL same-machine.** Giả định client (Claude Code) và Revit cùng một máy. Response trả **path tuyệt đối** để agent tự đọc bằng công cụ của nó. Client remote vẫn nhận `preview` + `schema` nhưng **không đọc được file** — ghi rõ hạn chế này trong mô tả tool, không cố stream file qua MCP.
2. **Bật spill: tham số `output` = `inline` (mặc định) | `file`** cho 8 tool nhóm 4. Mặc định `inline` → giữ nguyên hành vi B1 (chặn/summary). `send_code` **KHÔNG** có tham số này — nó **auto-spill theo size** (§6).
3. **Dọn file:** mỗi lần spill, xoá file cũ hơn **24 giờ** VÀ giữ tối đa **~50 file mới nhất** trong thư mục spill.

## B2.1 — Tool nhóm 4 và format (từ khảo sát §3)

| Tool | Format | Ghi chú |
|---|---|---|
| `compute_room_finishes` | SQLite | dataset quan hệ room×finish |
| `export_room_data` | SQLite | bulk room, hiện chưa có scope |
| `get_material_takeoff` | SQLite | takeoff toàn model theo material/category |
| `workflow_takeoff_report` | SQLite | báo cáo element/material/quantity nhiều category |
| `batch_execute` | NDJSON | tối đa 20 sub-result khác schema |
| `export_shared_parameter_file` | JSON | groups/definitions/bindings lồng nhau |
| `run_baked_tool` | NDJSON/JSON/text | output phụ thuộc code người dùng |
| `workflow_data_roundtrip` | NDJSON | báo cáo thay đổi từng row |

## B2.2 — Hạ tầng spill (TDD, test không cần Revit)

Thêm `src/shared/Infrastructure/ResponseSpillWriter.cs` (format-agnostic) + writer theo format:
- **Thư mục:** `%LOCALAPPDATA%\RvtMcp\spill\`. Tạo nếu chưa có.
- **Tên file:** `{command}_{pid}_{utcTimestamp}_{shortHash}.{ext}` — tránh đụng giữa các phiên.
- **SQLite:** một bảng cho mỗi collection; cột suy từ DTO (typed). Kèm mô tả `schema` (bảng→cột) trong response.
- **NDJSON:** một record/dòng. `JSON`: một mảng/đối tượng gốc. `text`: raw.
- **Dọn file:** gọi cleanup (xoá >24h + cap 50 file mới nhất) **mỗi lần** ghi spill mới.
- **Execution model:** spill được ghi đồng bộ trên Revit UI thread qua `ExternalEvent`; export opt-in vài chục nghìn row có thể làm UI khựng ngắn trong lúc ghi file.
- **KHÔNG** bundle Python bắt buộc. Response có thể kèm 1 dòng gợi ý cách query (SQL cho SQLite / jq cho NDJSON).

### Response envelope khi `output=file`
```jsonc
{
  "success": true,
  "output_mode": "file",
  "path": "C:\\Users\\...\\RvtMcp\\spill\\get_material_takeoff_1234_20260821T....sqlite",
  "format": "sqlite",
  "byte_count": 4823019,
  "schema": { "takeoff": ["element_id","category","material","area_mm2","volume_mm3"] },
  "record_count": 51234,
  "preview": "…first ~32–50 KiB (SQLite: preview vài chục row đầu dạng JSON)…",
  "preview_truncated": true,
  "note": "Local file. Query with SQL/jq; do not re-call this tool for the full set."
}
```
**Bắt buộc:** envelope này (không tính file trên đĩa) phải **tự nằm dưới budget 700 KiB** — preview phải cắt.

## B2.3 — `send_code` (§6, auto)

- Kiểm size output **tại boundary**. Nếu compact > `EnforcementBudgetBytes` (700 KiB): auto-spill.
- Format: sniff nội dung → NDJSON nếu là mảng đồng nhất, JSON nếu object, else `text`. **Không** ép SQLite.
- Trả `path` + `byte_count` + `preview` (~32–50 KiB đầu) + hướng dẫn. **Không bao giờ** biến execution đã chạy thành `success=false`.
- `run_baked_tool` dùng chung cơ chế auto này (đã đánh dấu `IsMutationOutcomeIndeterminate`).

## B2.4 — DoD Bước 2

- [x] `ResponseSpillWriter` + writer SQLite/NDJSON/JSON/text, có test (không cần Revit).
- [x] 8 tool nhóm 4 nhận `output=inline|file`; mặc định `inline` giữ nguyên hành vi B1; schema tool cập nhật.
- [x] `output=file` trả envelope đúng shape, envelope tự dưới budget, preview cắt đúng.
- [x] Cleanup >24h + cap 50 file, có test cho logic chọn file xoá.
- [x] `send_code` + `run_baked_tool`: auto-spill khi >budget, không `success=false`, có test.
- [x] Golden tool-list cập nhật cho param `output` mới.
- [x] Toàn bộ 468 test xanh. **CHỐT DỪNG #3** — User đã review và duyệt hoàn tất.
