# Product decisions close-out (2026-07-15)

Session waves A → C on `rvt-mcp`. Canonical tracker: `docs/analysis/2026-07-15-master-checklist.md`.

## Posture

| Rule | Detail |
|---|---|
| **Typed tools cover common project workflows** | Current surface: 40 tools by default; 229 with `--toolsets all` (232 with adaptive bake). Prefer typed tools when they exist. |
| **Out of typed scope → `revit_send_code_to_revit`** | C# only (Roslyn in plugin). No Python host. No new toolset for every gap. |
| **Do not grow the default catalog lightly** | Full tool schemas cost ~25k+ tokens for agents that inject `tools/list`. New surfaces need a strong reason + preferably **opt-in toolsets**. |

## Wave outcomes

| Wave | Decision |
|---|---|
| **A1 Toast** | **Shipped / PASS.** Result-only completion toasts; capture thumbnail; size cap; default **OFF**; ribbon toggle + `enableToast` / `BIMWRIGHT_ENABLE_TOAST`. Status dialog lists toast ON/OFF. |
| **A2–A4 Bake + send_code privacy** | **PASS.** Adaptive bake opt-in; body cache/persist opt-in; no VS required for bake. |
| **A5 Status flags** | **PASS.** Ribbon Status shows toolbaker / adaptive / cache / persist journal (read-only). No privacy toggle ribbon. |
| **B Family authoring (#7)** | **PASS (will not implement this cycle).** Project `families` management stays. No `family-authoring` / `revit_family_*` tools. Family Editor / definition authoring → **`send_code`**. Design/spike docs retained for a future revisit. |
| **C1 Python send_code** | **PASS (will not do).** Escape hatch remains **C# only**. |
| **C2 Revit Viewer** | **PASS (will not do).** Full Revit desktop only. No Viewer discovery/product path. |

## Escape hatch reminder

- MCP tool: `revit_send_code_to_revit` (toolset `toolbaker`, default **on**).
- Hide with `--read-only` or `--disable-toolbaker`.
- Body logging for bake/troubleshoot is **separate** and default **off** (`cacheSendCodeBodies`, `persistSendCodeBodies` + TTL). See `docs/bake.md`.
- Adaptive suggestions need `enableAdaptiveBake` (+ cache for send_code clusters).

## Toast (user-facing facts)

| Fact | Value |
|---|---|
| Default | **OFF** |
| Enable | Ribbon **Toast** button, or JSON `enableToast: true`, or env `BIMWRIGHT_ENABLE_TOAST=1` |
| Behavior | Only **completed** calls (no in-progress toast) |
| Capture | Success toast can show thumbnail if path under allowlist (`%TEMP%` / captures) |
| send_code on toast | Result redacted for privacy on that tool |
| Inspect flags | Ribbon **Status** → Privacy & bake section |

## Explicit non-goals (this close-out)

- Family document authoring tool suite (#7) — deferred indefinitely for context-cost reasons unless product reopens.
- Python / IronPython in-process execution.
- Autodesk Revit **Viewer** as a host target.
- New ribbon buttons that enable body-cache or persist “risk” modes.

## Still open (not decided here)

- KEI toolset uncommitted on working tree (public vs private vs opt-in).
- CHANGELOG formal **v0.6.0** tag when ready (toast/BCD already on `master`; Status privacy section may ship with plugin deploy).
- Upstream GitHub issue #7 may stay open for external tracking; implementation not scheduled.
