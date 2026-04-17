# Haiku benchmark — runtime template

**Instructions for Claude (executing this template):**

You are running the bimwright Haiku benchmark. Follow these steps exactly.

## Step 1 — Identify current state

Run:

```bash
cd /d/Projects/bimwright && git rev-parse --short HEAD
```

Call this `<commit>`. Read the version from `src/server/Bimwright.Rvt.Server.csproj` `<Version>` element — call this `<version>`.

## Step 2 — Load the tool surface

Read `tests/Bimwright.Rvt.Tests/Golden/tools-list.json`. Extract the list of tools with their names and input schemas. Cross-reference the `[Description]` attributes in `src/server/Program.cs` to recover the full description text for each tool (the golden file stores only a hash).

## Step 3 — Locate the most recent baseline

List `benchmarks/runs/*.md` by filename (they are date-prefixed). The most recent run is the current baseline. Read it and extract the per-query score table.

## Step 4 — Spawn the Haiku sub-agent

Use the `Agent` tool with `subagent_type: general-purpose`. Model: Haiku 4.5 (`claude-haiku-4-5-20251001`).

**Agent prompt:**

> You are a BIM user querying a Revit MCP server. I will give you the tool list + descriptions, followed by 10 queries in Vietnamese. For each query, pick exactly one tool (or a tool chain for multi-step queries) and produce the parameter JSON you would call it with. Reply in the format:
>
> ```
> Q<n>:
>   Tool: <tool_name>
>   Params: {...}
> ```
>
> Here is the tool list: *(paste RICH-style descriptions for each tool — pull from `Program.cs` `[Description]` attributes, augment where descriptions are thin)*
>
> Here are the queries:
>
> 1. Tìm tất cả tường cao hơn 3 mét
> 2. Ẩn tất cả cửa trong view hiện tại
> 3. Model này có bao nhiêu element?
> 4. Tạo tường từ (0,0) đến (5000,0) ở Level 1, cao 3000mm
> 5. Tạo sàn hình chữ nhật 6x4m ở tầng 2
> 6. Tôi đang xem view gì?
> 7. Tô màu tường theo vật liệu khác nhau
> 8. Xóa element 12345 và 67890
> 9. Tạo level mới ở cao độ 9000mm tên Level 4
> 10. Xuất danh sách tất cả phòng với diện tích

## Step 5 — Score the results

For each query, compare the Haiku agent's tool pick and parameter JSON to the expected tool + params in the baseline run file.

- **Tool selection:** 1 point if the tool name is correct.
- **Param accuracy:** 1 point if ALL param keys + value formats match. Partial credit 0.5 for off-by-one on a single key.

Produce two scores:

- Tool selection accuracy: `(sum / 10) × 100%`
- Param accuracy: `(sum / 10) × 100%`

## Step 6 — Compute delta vs baseline

For each query, record whether this run scored the same, better, or worse than the baseline's corresponding query. Flag any query where the delta is ≥ 15% (i.e. a previously-correct query is now wrong, or vice versa).

## Step 7 — Write the run file

Write to `benchmarks/runs/<YYYY-MM-DD>-<commit>-<version>.md`:

```markdown
# Haiku benchmark run — <date>

- Commit: <commit>
- Version: <version>
- Tool count: <n>
- Baseline compared: <path to previous run>

## Score

| Agent | Tool selection | Param accuracy | Overall |
|---|---|---|---|
| RICH (current) | X/10 | Y/10 | Z% |
| RICH (baseline) | X/10 | Y/10 | Z% |

## Drops vs baseline (Δ ≥ 15%)

- **Q<n> (<query>)** — current picked `<tool>`, baseline picked `<tool>`.
  Suspected cause: <analysis>
  Suggested action: <description edit, rename, new USE WHEN clause>

<If no drops: write "None">

## Raw results

<details>
<summary>Full query table</summary>

| # | Query | Expected tool | Haiku tool | Expected params | Haiku params | Score |
|---|---|---|---|---|---|---|
| 1 | ... | ... | ... | ... | ... | 1.0 |

</details>
```

## Step 8 — Summary in chat

Print:

- Overall score: tool-selection X%, param-accuracy Y%.
- Delta vs baseline: +/- N percentage points.
- Any queries with ≥ 15% drop (by query number).
- One-line merge recommendation: "Merge OK", "Flag in PR", or "Block — investigate".

Do not commit the run file automatically. The user will review and commit.
