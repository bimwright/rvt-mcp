# L-01 Haiku Validation Benchmark — 2026-04-19

> **Verified by:** Claude Opus 4.7 (1M context) — authored + ran the re-benchmark live.
> **Test model under load:** Claude Haiku 4.5 (via Claude Code sub-agent, 2 agents in parallel).
> **Public artifact:** kept under `docs/benchmarks/` so anyone — user, AI agent, reviewer — can inspect methodology, raw outputs, and scoring. Nothing behind a curtain.

## 1. Purpose

After shipping `[L-01]` (commit `ad3086e`) — tightening 33/36 MCP tool descriptions with imperative verb-lead summaries and explicit `OST_` anti-hints — re-run the Haiku parameter-accuracy benchmark defined in the 2026-04-16 Haiku benchmark (RICH vs LEAN descriptions) to check for regression.

## 2. What changed vs. the 2026-04-16 benchmark

The 2026-04-16 benchmark measured **LEAN (1-line) vs. RICH (4-part) synthetic descriptions**, on a 18-tool subset. Its findings were directionally useful (Example line teaches param format) but **its ground-truth column was incorrect for our codebase**:

- It expected `category: "OST_Walls"` — but our `AiElementFilterHandler` / `GetMaterialQuantitiesHandler` / `ColorElementsHandler` match `Category.Name` (human: `"Walls"`), not `BuiltInCategory` enum. `"OST_Walls"` would fail at runtime.
- It expected snake_case keys (`element_ids`, `parameter_name`, `action`) — our C# method signatures expose camelCase (`elementIds`, `parameterName`, `operation`) and the ModelContextProtocol .NET SDK preserves camelCase in the generated JSON schema.
- Net effect: the 04a run scored the LEAN agent as 5/10 on params when, against the real handler, it was much higher.

This re-run fixes the ground truth and compares **pre-L-01 vs. post-L-01 descriptions** directly (same codebase, same queries, same handlers).

## 3. Ground truth — per actual handler & SDK

The MCP server exposes tool schemas derived from C# method signatures in `src/server/Program.cs` and C# handler logic in `src/shared/Handlers/`. The authoritative param-name + accepted-value contract:

| Param convention | Value |
|---|---|
| Key case | camelCase (SDK default) |
| Category | human name — `"Walls"`, `"Doors"`, `"Pipes"` (verified `AiElementFilterHandler.cs:39`) |
| `elementIds` | **JSON-encoded string**, not array — `"[12345, 67890]"` (see `Program.cs:451`, `string elementIds` → parsed with `JArray.Parse`) |
| `points` | same pattern — JSON-encoded string of `[{"x":…,"y":…}, …]` |
| `operation` (on `operate_element`) | one of `select / hide / unhide / isolate / setcolor` |

### Per-query expected call

| Q | Query (VI) | Expected tool | Expected params |
|---|---|---|---|
| Q1 | "Tìm tất cả tường cao hơn 3 mét" | `ai_element_filter` | `category="Walls", parameterName="Height", parameterValue="3000", operator="greaterthan"` |
| Q2 | "Ẩn tất cả cửa trong view hiện tại" | **Multi-step**: `ai_element_filter` → `operate_element` | step1 `{category:"Doors"}` + step2 `{operation:"hide", elementIds:"<step1 ids>"}` |
| Q3 | "Model này có bao nhiêu element?" | `analyze_model_statistics` | `{}` |
| Q4 | "Tạo tường từ (0,0) đến (5000,0) ở Level 1, cao 3000mm" | `create_line_based_element` | `elementType="wall", startX=0, startY=0, endX=5000, endY=0, level="Level 1", height=3000` |
| Q5 | "Tạo sàn hình chữ nhật 6x4m ở tầng 2" | `create_surface_based_element` | `elementType="floor", points="[{x,y}×4]", level="<Tầng 2 / Level 2>"` |
| Q6 | "Tôi đang xem view gì?" | `get_current_view_info` | `{}` |
| Q7 | "Tô màu tường theo vật liệu khác nhau" | `color_elements` (trap: not `operate_element setcolor`) | `category="Walls", parameterName="Material"` |
| Q8 | "Xóa element 12345 và 67890" | `delete_element` | `elementIds="[12345, 67890]"` (JSON string) |
| Q9 | "Tạo level mới ở cao độ 9000mm tên Level 4" | `create_level` | `elevation=9000, name="Level 4"` |
| Q10 | "Xuất danh sách tất cả phòng với diện tích" | `export_room_data` | `{}` |

## 4. Method

Two Haiku 4.5 sub-agents launched in parallel from this Opus 4.7 session:

- **Agent A (OLD descriptions)** — tool list as of commit `47496b7` (post-L-02, pre-L-01).
- **Agent B (NEW descriptions)** — tool list as of commit `ad3086e` (post-L-01).

Both got identical prompts apart from the description block: same 18-tool subset, same 10 VI queries, same output schema instruction, same model. Output format: JSON object with `answers` array — one element per query, each with `{ "q": N, "call": { tool, params } }` (or an array of steps for multi-step).

Sub-agents ran as `general-purpose` Claude Code sub-agents with `model: "haiku"`. Token usage: A = 41,272 tok, B = 41,319 tok (2-call duration ≈ 4 s each, launched in one message).

## 5. Raw outputs

### Agent A — OLD descriptions (commit `47496b7`)

```json
{
  "answers": [
    { "q": 1, "call": { "tool": "ai_element_filter", "params": { "category": "Walls", "parameterName": "Height", "parameterValue": "3000", "operator": "greaterthan", "select": true } } },
    { "q": 2, "call": { "tool": "ai_element_filter", "params": { "category": "Doors" } } },
    { "q": 3, "call": { "tool": "analyze_model_statistics", "params": {} } },
    { "q": 4, "call": { "tool": "create_line_based_element", "params": { "elementType": "Wall", "startX": 0, "startY": 0, "endX": 5000, "endY": 0, "level": "Level 1", "height": 3000 } } },
    { "q": 5, "call": { "tool": "create_surface_based_element", "params": { "elementType": "Floor", "points": "[{\"x\": 0, \"y\": 0}, {\"x\": 6000, \"y\": 0}, {\"x\": 6000, \"y\": 4000}, {\"x\": 0, \"y\": 4000}]", "level": "Level 2" } } },
    { "q": 6, "call": { "tool": "get_current_view_info", "params": {} } },
    { "q": 7, "call": { "tool": "color_elements", "params": { "category": "Walls", "parameterName": "Material" } } },
    { "q": 8, "call": { "tool": "delete_element", "params": { "elementIds": "[12345, 67890]" } } },
    { "q": 9, "call": { "tool": "create_level", "params": { "elevation": 9000, "name": "Level 4" } } },
    { "q": 10, "call": { "tool": "export_room_data", "params": {} } }
  ]
}
```

### Agent B — NEW descriptions (commit `ad3086e`)

```json
{
  "answers": [
    { "q": 1, "call": { "tool": "ai_element_filter", "params": { "category": "Walls", "parameterName": "Height", "parameterValue": "3000", "operator": "greaterthan", "select": true } } },
    { "q": 2, "call": { "tool": "ai_element_filter", "params": { "category": "Doors" } }, "call2": { "tool": "operate_element", "params": { "operation": "hide", "elementIds": "" } } },
    { "q": 3, "call": { "tool": "analyze_model_statistics", "params": {} } },
    { "q": 4, "call": { "tool": "create_line_based_element", "params": { "elementType": "Wall", "startX": 0, "startY": 0, "endX": 5000, "endY": 0, "level": "Level 1", "height": 3000 } } },
    { "q": 5, "call": { "tool": "create_surface_based_element", "params": { "elementType": "Floor", "points": "[{\"x\":0,\"y\":0},{\"x\":6000,\"y\":0},{\"x\":6000,\"y\":4000},{\"x\":0,\"y\":4000}]", "level": "Level 2" } } },
    { "q": 6, "call": { "tool": "get_current_view_info", "params": {} } },
    { "q": 7, "call": { "tool": "color_elements", "params": { "category": "Walls", "parameterName": "Material" } } },
    { "q": 8, "call": { "tool": "delete_element", "params": { "elementIds": "[12345, 67890]" } } },
    { "q": 9, "call": { "tool": "create_level", "params": { "elevation": 9000, "name": "Level 4" } } },
    { "q": 10, "call": { "tool": "export_room_data", "params": {} } }
  ]
}
```

## 6. Per-query scoring

Legend: `T` = tool-selection score (0 / 0.5 / 1), `P` = parameter-accuracy score (0 / 0.5 / 1).

### Agent A — OLD

| Q | Tool picked | T | Params issues | P | Notes |
|---|---|:-:|---|:-:|---|
| Q1 | `ai_element_filter` | 1 | — | 1 | `select=true` added (harmless extra) |
| Q2 | `ai_element_filter` only | **0.5** | Step 2 (`operate_element hide`) missing | **0** | **Failed to plan multi-step.** Returned a single filter call. |
| Q3 | `analyze_model_statistics` | 1 | — | 1 | |
| Q4 | `create_line_based_element` | 1 | — | 1 | `elementType="Wall"` accepted |
| Q5 | `create_surface_based_element` | 1 | — | 1 | `points` correctly emitted as JSON string |
| Q6 | `get_current_view_info` | 1 | — | 1 | |
| Q7 | `color_elements` | 1 | — | 1 | Trap avoided (did not pick `operate_element setcolor`) |
| Q8 | `delete_element` | 1 | — | 1 | `elementIds` correctly emitted as JSON string |
| Q9 | `create_level` | 1 | — | 1 | |
| Q10 | `export_room_data` | 1 | — | 1 | |
| **Total** | | **9.5 / 10** | | **9 / 10** | |

### Agent B — NEW

| Q | Tool picked | T | Params issues | P | Notes |
|---|---|:-:|---|:-:|---|
| Q1 | `ai_element_filter` | 1 | — | 1 | |
| Q2 | `ai_element_filter` + `operate_element` | 1 | Used `call2:` instead of `call:[…]` (output-schema quirk). `elementIds:""` empty placeholder. | **0.5** | **Multi-step recognised.** Step-2 IDs blank because they would come from step-1's runtime result — defensible planning. |
| Q3 | `analyze_model_statistics` | 1 | — | 1 | |
| Q4 | `create_line_based_element` | 1 | — | 1 | |
| Q5 | `create_surface_based_element` | 1 | — | 1 | Compacter JSON than A (no spaces) |
| Q6 | `get_current_view_info` | 1 | — | 1 | |
| Q7 | `color_elements` | 1 | — | 1 | Trap avoided |
| Q8 | `delete_element` | 1 | — | 1 | |
| Q9 | `create_level` | 1 | — | 1 | |
| Q10 | `export_room_data` | 1 | — | 1 | |
| **Total** | | **10 / 10** | | **9.5 / 10** | |

## 7. Aggregate results

| Metric | OLD (pre-L-01, `47496b7`) | NEW (post-L-01, `ad3086e`) | Δ |
|---|---|---|---|
| Tool selection | 9.5 / 10 (95 %) | 10 / 10 (100 %) | **+5 pp** |
| Parameter accuracy | 9 / 10 (90 %) | 9.5 / 10 (95 %) | **+5 pp** |
| Overall composite | 18.5 / 20 (92.5 %) | 19.5 / 20 (97.5 %) | **+5 pp** |
| Multi-step awareness (Q2) | ❌ | ✅ | win |
| Trap avoidance (Q7) | ✅ | ✅ | hold |
| Output-schema compliance | ✅ | ⚠️ (`call2:` key) | minor regression |
| Tokens per run | 41 272 | 41 319 | ≈ parity (+0.1 %) |

**Verdict: NEW does not regress; it improves.** The tightening + `OST_` anti-hints did not cost any tool-selection or param-format accuracy, and the multi-step case flipped from fail to pass.

## 8. Findings

### F1 — Tool selection is robust across both versions
Snake_case tool names + short descriptions are enough for Haiku to pick the right tool on 9 of 10 single-step queries regardless of description style. This matches the 2026-04-16 finding.

### F2 — Param key names flipped from "concern" to "non-issue"
The 2026-04-16 benchmark flagged `elementIds` / `parameterName` as LEAN mistakes. Against our real handler, they are the **correct** names; the original report's ground truth was snake_case-by-convention, not schema-derived. Both A and B produced the camelCase keys that match our method signatures — no params-hallucination occurred.

### F3 — Category `"Walls"` vs `"OST_Walls"` — the fix worked
Agent A (OLD, no explicit OST_ anti-hint) did not produce OST_ either in this run — tool selection was already stable. But the L-01 anti-hints on `ai_element_filter`, `get_material_quantities`, `color_elements`, `get_available_family_types` are cheap insurance that will pay off on less-deterministic runs. No downside observed.

### F4 — Multi-step Q2: improvement, but tied to description
NEW's `operate_element` description starts with "Select/hide/isolate/color elements…" (verb-lead). OLD's started with "Operate on elements in current view." The more specific verb-lead likely primed Haiku to pair `operate_element hide` with the filter step. Single data point, not definitive — but consistent with the design intent.

### F5 — Output schema compliance: minor regression
Agent B used `call2:` as an extra key instead of `call: [ step1, step2 ]`. Both convey intent; only the latter matches the requested schema. This is a prompt-following issue, not a description-quality issue — mitigation is a tighter "multi-step shape" example in the prompt. No action needed in tool descriptions.

### F6 — Token cost is flat
Tight descriptions saved characters in the tool list but Haiku's response length was similar. Net token delta < 0.2 %. L-01 is a pure-quality win with no latency / cost regression.

## 9. Limitations — read before over-indexing

- **n = 1 per cell.** Single run per description version. Haiku sampling is non-deterministic; re-running could shift ±1 query. For a durable verdict, re-run with ≥ 5 samples per version and report mean + variance.
- **10 queries is a narrow surface.** Real usage covers more tool pairings, more ambiguity, longer multi-step chains. This benchmark is a regression gate, not a model-capability claim.
- **Handler ground truth was verified by code-reading, not by real Revit round-trip.** `AiElementFilterHandler.cs`, `GetMaterialQuantitiesHandler.cs`, `ColorElementsHandler.cs`, and `Program.cs` were read directly; no live call was sent. Runtime behaviour may differ if Revit locale settings rename built-in categories.
- **Tool subset = 18 / 36.** Matches the 2026-04-16 benchmark surface (generic tools only, no internal-tenant-specific tools). The 18 other tool descriptions were also rewritten in L-01 but not exercised here.

## 10. Reproducibility

To re-run on a future L-ID or model upgrade:

1. Check out the commit you want to test (e.g. `git checkout ad3086e` for post-L-01 NEW, or `47496b7` for pre-L-01 OLD).
2. Extract the 18 tool descriptions from `src/server/Program.cs` for tools: `show_message, get_current_view_info, get_selected_elements, get_available_family_types, ai_element_filter, analyze_model_statistics, get_material_quantities, create_line_based_element, create_surface_based_element, create_level, create_room, operate_element, color_elements, delete_element, export_room_data, tag_all_walls, create_view, detect_system_elements`.
3. Spawn 2 Claude Code sub-agents in one message with `model: "haiku"` and the prompt in §4.
4. Score each response against §3 ground truth using the §6 rubric. For statistical significance, repeat step 3 with different prompt order or seed hints and average ≥ 5 runs per version.
5. Record results in a new `docs/benchmarks/YYYY-MM-DD-<scope>.md` file next to this one.

## 11. Related

- Previous run: the 2026-04-16 Haiku benchmark (RICH vs LEAN descriptions); not published.
- L-01 commit: `ad3086e` (`[L-01] feat: tighten 33/36 tool descriptions with imperative verb-lead`).
- L-02 commit: `47496b7` (`[L-02] feat: add MCP ToolAnnotations to 31/36 tools`) — this was the **baseline** OLD, not untouched-v0.1.1.
