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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-233%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  English · <a href="README.vi.md">Tiếng Việt</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>
</p>

---

## What it is

`rvt-mcp` is a **local** bridge between an MCP client and a running Revit session. A .NET 8 server talks MCP over stdio; a thin add-in per Revit year (2022–2027) runs inside Revit and is reached over localhost TCP (≤2024) or a named pipe (≥2025). Nothing leaves the machine, it is C# end to end, and lengths are millimetres at the tool boundary. Details: [ARCHITECTURE.md](ARCHITECTURE.md).

Agents get a **typed tool surface** for common Revit work, a C# escape hatch for everything else, and an **optional** way to turn repeated patterns into personal tools (ToolBaker): start from a shared runtime and grow *your* tools on top. Family Editor authoring is out of scope for now.

---

## Install

**Let an AI agent install it for your target client.** Use an agent that can run PowerShell and edit local files on this Windows machine (Claude Code, Codex, Cursor, …), not a chat session without local tools. The agent installing it can be different from the client where you will use Revit tools. It follows [AGENTS.md](AGENTS.md), previews changes and asks before applying them; you may still need to approve a UI step or restart an app.

For **Claude Desktop**, paste:

```text
Install rvt-mcp for Claude Desktop on this Windows machine.
First read https://github.com/bimwright/rvt-mcp/blob/master/AGENTS.md.
Configure only Claude Desktop, not other clients. Inspect existing installations,
preview changes and ask before writing. Tell me if I need to use the UI or restart an app.
```

For another client, replace **Claude Desktop** with its name. **Claude Code and Claude Desktop are different targets:** installer option `claude` means Code; `claude-desktop` means Desktop.

**Or run the installer yourself (Claude Desktop, direct config).** Close Revit and fully quit Claude Desktop (including its tray process), then in PowerShell:

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/bimwright/rvt-mcp/releases/latest).tag_name
$dir = "$env:TEMP\RvtMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/bimwright/rvt-mcp/releases/download/$tag/RvtMcp.Setup-$tag-win-x64.zip" -OutFile "$dir.zip"
Expand-Archive "$dir.zip" -DestinationPath $dir -Force
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf -Client claude-desktop
# Review the preview before applying:
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client claude-desktop
```

This installs the add-in for every detected Revit 2022–2027 and registers `rvt-mcp` only in Claude Desktop (the config is backed up first). For Claude Code use `-Client claude`; for multiple requested clients use a comma list. Omitting `-Client` wires every detected client; `-Client none` leaves configs untouched. The server lives at `%LOCALAPPDATA%\Bimwright\rvt-mcp\server\current\rvt-mcp.exe` ([per-client steps, including Desktop classic/MSIX paths](docs/mcp-client-wiring.md)).

**Check it works:** restart your AI client, open a model in Revit, start MCP from the ribbon (**Add-Ins** → **RvtMcp**) and ask the agent to call `revit_get_current_view_info`. It should return the active view's name and type.

**Upgrade:** run the new release's installer the same way — no uninstall first; clients only need a restart. **Uninstall:** `uninstall.ps1 -Yes` in the same folder removes the add-ins and the server (settings stay unless you add `-Purge`); run `install.ps1 -Uninstall -Client auto` first to also remove the client entries. More, including developer and NuGet installs: [docs/install.md](docs/install.md).

**Upgrading from v0.8.1 or v0.6.x:** those versions keep your settings and ToolBaker data in `%LOCALAPPDATA%\RvtMcp\`; v1.0.0 uses `%LOCALAPPDATA%\Bimwright\rvt-mcp\`, and the installer moves the old folder there. Close Revit and every MCP client first, because a running old server locks the folder. Check in PowerShell with `Test-Path "$env:LOCALAPPDATA\RvtMcp"`. If the installer stops with `Both … exist` or `Could not move …`, it has undone what it moved: keep the folder that holds your settings and ToolBaker data, **rename the other one (for example to `RvtMcp.bak`) instead of deleting it**, close the clients that were using it, and run the installer again.

**Claude Desktop extension (MCPB, optional):** direct config above is the default for agent-assisted installs. If you want the extension/settings UI, use Setup with `-Client none`, then install the `.mcpb` through Desktop's extension UI. **Choose one route, not both.** `-Client none` does not remove an existing manual registration. The v1.0.0 extension is unsigned, does not install the gateway/add-ins, and requires the exact server build from the same release. See [MCPB installation and settings](docs/install.md#claude-desktop-mcpb).

---

## Videos

Community videos of rvt-mcp at work. Install steps shown in a video can be older than this README — follow the steps above.

- [Connecting ChatGPT Astra to Revit | Testing AI Changes in a Real Project](https://www.youtube.com/watch?v=J-i057B-3dI) — Revit Mentor
- [Exploring Revit + GPT 6 Astra](https://www.youtube.com/watch?v=2_W1uLn6s_I) — BIM Pure
- [GPT-6 Astra Built a Revit House in 14 Minutes](https://www.youtube.com/watch?v=pkFnSQ9Bapg) — Archi Vlogs

---

## Tools

| Mode | Tools | Notes |
|------|------:|-------|
| Fresh install | **233** | `install.ps1` seeds `"toolsets": ["all"]` in `rvtmcp.config.json` |
| Bare `rvt-mcp.exe` | **46** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **233** | Full catalog |
| `all` + adaptive bake | **236** | Adds 3 suggestion-lifecycle tools |

Counts exclude your personal baked tools. The installer writes the seeded default only when `rvtmcp.config.json` doesn't already set `toolsets` — your own list survives upgrades, and removing the key (or setting your own CSV) returns a bare server to the 46-tool surface. Read-only filters individual tools by `ReadOnly=true`, including reads inside mixed toolsets. Tools that can write files are excluded even when their default output is inline.

| Toolset | What it covers |
|---------|----------------|
| `query` | View, selection, filters, stats, parameters, relationships, worksets, groups/assemblies |
| `create` | Grids, levels, rooms, line/point/surface-based elements, groups |
| `view` | Create views, sheets layout helpers, capture image, crop/scale |
| `meta` | Batch execute (max 20), multi-Revit targets, recent models, project info, purge unused (MVP), message, send_code |
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
| `sheets` | Sheets, titleblocks, revisions, renumber, viewport layout |
| `materials` | Materials, appearance, assignment, takeoff |
| `geometry` | BBox, measure, clash, volume/area, … |
| `rooms` | Rooms/areas/spaces, finishes, separators |
| `links` | Revit/CAD links, coordinate audit, acquire/publish coordinates |
| `parameters` | Project/shared parameters |
| `organization` | Saved selections, view templates |
| `workflows` | Composite clash/audit/sheet/takeoff-style flows |
| `structural` | Columns, beams, foundations, rebar, loads, … |

### send_code, ToolBaker, ribbon and languages

- **`revit_send_code_to_revit`** (on by default) compiles and runs a C# body inside Revit when no typed tool fits; `--read-only` or `--disable-send-code` removes it. See [docs/send-code.md](docs/send-code.md), and [docs/stairs-workflow.md](docs/stairs-workflow.md) for stairs.
- **ToolBaker:** `revit_list_baked_tools` / `revit_run_baked_tool` need `--toolsets toolbaker`. Adaptive bake (`--enable-adaptive-bake`, off by default) suggests tools from repeated calls; nothing is added until you accept one. Bake compiles inside Revit — no Visual Studio needed. See [docs/bake.md](docs/bake.md).
- **Ribbon:** start or stop the connection, open **History** to search and re-run past calls, and switch completion **Toasts** (on by default).
- **UI languages:** the add-in UI comes in 15 languages and follows Revit's UI language; change it with the **Language** button in the ribbon slide-out — it opens **Settings → General → Language** (`BIMWRIGHT_UI_LANGUAGE` still wins). Tool names and payloads stay English. See [docs/localization.md](docs/localization.md).

### Why activity toasts exist

Toasts are work feedback, not just decoration. They grew out of three practical needs:

- **Free users from watching the chat.** In real-world MCP workflows, an AI agent can work for a long time while Revit gives little visible feedback. Watching the chat just to check whether the agent is doing anything wastes attention. The activity card reports completed tool calls so users can turn to other work between updates.
- **Support multitasking.** The maintainer develops and repeatedly tests several desktop applications in parallel. Compact notifications make it easier to follow those sessions without keeping every chat in view.
- **Modernize the experience.** Revit-side feedback makes automation feel more responsive and understandable, without interrupting work with modal dialogs.

A toast reports a tool result, **not progress inside a running tool or completion of the entire task**. Repeated results share one card; notifications may wait while Revit is minimized or blocked by a modal dialog. They do not replace reviewing the agent's work.

Each card names the gateway and Revit year (for example `rvt-mcp 2022`), the latest tool, and the Success · Failed · Capture counts. A capture preview stays on the card for at least 5 seconds.

Deliberate hover opens an activity timeline showing the newest three results, each with its local completion time (`HH:mm:ss`). Scroll up for earlier calls in that card. Incoming results follow the bottom with a short glide-in (the rows rise, the new row fades in and its outcome dot pops; none of this with reduced motion); while you read older calls, your position stays put. Click inside the timeline to read without closing it; clicking the rest of the card opens History. This works with branding off. Summaries are bounded and redacted, stay in memory only, and clear when the card closes. Script objects/arrays show counts; an incomplete survey remains explicitly incomplete. Server-local tools do not gain toast coverage from this UI change.

Notifications are **on by default** and can be turned off. In **Settings → Toast**, choose the idle duration (10/20/30/60 seconds; default 20) and **Show branding** (**off by default**), which shows the wordmark on hover. The choice applies immediately and is saved across Revit restarts; users do not need to display branding to get activity feedback.

**Position:** choose **Horizontal alignment** (Left/Right) and **Vertical alignment** (Top/Bottom) independently in **Settings → Toast**; the default is top-left. Changes apply to the open card and are saved immediately, and changing a corner clears any saved drag position. Turn on **Allow dragging the card** to move it by its title row; a plain click still opens History. Turning drag off keeps the saved position, turning it on restores it, and **Reset position** clears it. The position is stored relative to the Revit window and kept inside the monitor's work area. A card anchored at the bottom grows upward, and falls back to the other side when there is no room. Motion follows the Windows animation setting. A failed save is shown under the setting while the choice stays active for the session. Hovering the latest-tool line or a timeline row shows the sanitized result, Success/Failed, the completion time and the measured duration when available. Coordination between toasts of several Autodesk applications is not part of this.

### Prompts

v1.0.0 provides six MCP prompts — pick `/mcp__rvt-mcp__revit_<name>` (Claude Code) or the prompts menu (Claude Desktop), and the agent follows the script with the tools it already has:

- `revit_getting_started` — orient in the open model (read-only, works on defaults).
- `revit_drawing_layout` — supply `request`: arrange the viewports of one sheet. Reads real sheet-space positions first (`revit_get_viewport_geometry`), agrees the alignment with you, shows a dry-run plan (`revit_align_viewports`), moves only after you confirm, then reads back. Needs `query,sheets,view,meta`. In read-only mode it reports and proposes only.
- `revit_change` — supply `change`: survey relationships, agree the smallest scope for this request, confirm the concrete proposal before writing, then read back and record the reason in the conversation. Needs `query,meta`; works without `send_code`. In read-only mode it stops at the survey/proposal. Missing or incomplete evidence stays "not checked"; readback is not a complete inventory of indirect changes, and the record is not a persistent change database.
- `revit_model_audit` — health audit: warnings, families, dry-run purge candidates (needs `workflows,families,lint,meta`).
- `revit_pre_issue_check` — checks resolved sheets before issue (needs `sheets,view,annotation,lint,meta`). Supply sheet numbers/IDs, an explicit number/name filter, or `all`; a named sheet set needs its member sheets. Sampled model warnings and incomplete checks are reported as **NOT VERIFIED**, not a sheet-level pass.
- `revit_stairs` — guided stair creation through `send_code` (writes only after your confirmation). Includes the transaction/failure/cleanup template; no source checkout is needed.

If a prompt's toolsets aren't enabled, it answers with the exact `--toolsets` line to add — nothing runs half-configured. Read-only protection stays on when the missing tools allow it; prompts requiring write-capable toolsets explain the conflict rather than silently changing configuration. Prompts are instructions for the agent, not server-enforced workflow locks.

---

## Configuration

Since v1.0.0 the server reports `_changes` and local per-model `_history`. History defaults on independently of call logs; use `--disable-change-history` to disable recording. The `meta` tools `revit_record_change`, `revit_get_change_records` and `revit_resolve_history_identity` attach reasons to explicit call IDs, query stored changes and record the owner's history choice after a copy or Save As. See [change tracking](docs/change-tracking.md) for privacy, limits and recovery.

`revit_survey_change_impact` is a tool in `query`: a bounded, read-only survey of ten relationship groups, including with send-code disabled. `scopeThreshold` is required for each request; view/schedule iteration is opt-in through `maxViews > 0`. Partial results remain explicit and parameter observations are not trusted history snapshots. See [change-impact survey](docs/change-survey.md).

Precedence, high wins: **CLI → env (`BIMWRIGHT_*`) →** `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json`.

| Setting | CLI | Env | JSON |
|---------|-----|-----|------|
| Target year | `--target 2024` | `BIMWRIGHT_TARGET` | `target` |
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
| Spill retention (default 36 h) | `--spill-retention-hours <n>` | `BIMWRIGHT_SPILL_RETENTION_HOURS` | `spillRetentionHours` |
| LAN bind (plugin) | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker surface | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache send_code bodies (bake clusters) | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| Persist send_code journal | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| Completion toast (default on) | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| Toast branding (default off, saved) | Settings → Toast → **Show branding** | — | `showBranding` |
| Toast idle duration (default 20 s) | Settings → Toast → **Idle duration** | — | `toastIdleSeconds` |
| Toast position (default top-left, no drag; saved) | Settings → Toast → **Horizontal / Vertical alignment**, **Allow dragging the card**, **Reset position** | — | `toastHorizontalAlign`, `toastVerticalAlign`, `toastDragEnabled`, `toastDragOffset` |
| UI language (add-in) | ribbon **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

After changing server flags, restart the MCP connection so the client picks up the new tool list.

---

## Permissions & auto mode

These controls arrive in v1.0.0. The v0.8.1 package does not include the new switches or per-tool read-only filtering. `send_code` and `run_baked_tool` must be called directly; `batch_execute` rejects them.

MCP annotations describe each tool's document/file effects. Transient selection, active-view and zoom changes count as read-only. `send_code` has no annotations: keep it out of automatic permissions and confirm each arbitrary-code call. For Claude Code, copy only the read-only allow list below; do not allow the broad `mcp__rvt-mcp__*` wildcard. The list covers `--toolsets all`; your selected toolsets may expose fewer tools.

<details>
<summary>Read-only allow list (generated from annotations)</summary>

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
      "mcp__rvt-mcp__revit_switch_target",
      "mcp__rvt-mcp__revit_workflow_model_audit"
    ]
  }
}
```
<!-- END READ_ONLY_ALLOWLIST -->

</details>

send_code defaults **on**, independently of ToolBaker; call-log defaults **off**. CLI overrides environment variables, which override JSON. Authenticated server settings override plugin settings for that request only. Call-log off suppresses the server journal, plugin `mcp-calls.jsonl` and send-code body journal; in-memory History remains available. Body journaling needs both call-log on and its separate TTL opt-in. ToolBaker `usage.jsonl` is separate and follows adaptive-bake settings.

The response guard defaults **on**: UTF-8 warnings at 65536 bytes, strong warnings above 262144, a 716800-byte budget and a 1048576-byte transport cap. JSON escaping and MCP content/metadata are measured at the server. Oversized reads return `RESPONSE_TOO_LARGE` with narrowing guidance; completed writes return a compact summary. Arbitrary code output spills to a local file with `mutation_applied: null`; inspect the file instead of rerunning the command. A spill file is kept 36 hours by default (`--spill-retention-hours`, 1-8760; an invalid value uses 36) and no file-count cap deletes a younger file. Turning the guard off leaves the transport cap active. Byte limits must be integers >=1024, ordered `warn <= strong <= budget <= max`; adjust lower thresholds when lowering the budget.

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Full Revit desktop only; Revit Viewer is not a supported target. CI builds all six add-ins, but runtime depth varies by year — recheck baked tools and custom C# on the years you use.

---

## Security and privacy

- Local by default: loopback TCP or a local named pipe, with a per-session auth token in the discovery files under `%LOCALAPPDATA%\Bimwright\rvt-mcp\`.
- Tool arguments are schema-checked before handlers run; errors returned to the model are sanitized.
- `send_code` runs arbitrary C# in the Revit process — powerful and risky. Use `--read-only` or `--disable-send-code` if that is unacceptable.
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
| [CONTRIBUTING.md](CONTRIBUTING.md) | Build, test, add a tool |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

---

## Community contributions

Code, bug reports, reproducible examples and proposals help improve rvt-mcp. Thanks to:

| Contributor | Contribution |
|-------------|--------------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | Reproducible reports on MEP network membership and pipe system handling ([#11](https://github.com/bimwright/rvt-mcp/issues/11), [#12](https://github.com/bimwright/rvt-mcp/issues/12)). |
| [@razmikb](https://github.com/razmikb) | Hosted-family placement and stair/send-code failure reports that led to placement checks, helper-class support and broader failure-handling tests ([#13](https://github.com/bimwright/rvt-mcp/issues/13), [#14](https://github.com/bimwright/rvt-mcp/issues/14)). |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | Family Authoring Tool Suite proposal that helped clarify the roadmap and scope ([#7](https://github.com/bimwright/rvt-mcp/issues/7)). |
| [@PhanCongVuDuc](https://github.com/PhanCongVuDuc) | Pull request [#15](https://github.com/bimwright/rvt-mcp/pull/15): the fix that lets `send_code` run with no model open, the `revit_switch_target` hint fix, and the `revit_open_model` proposal, shipped in reworked form in v0.8.1. |

---

## The bimwright family

Open-source tools connecting AI assistants to BIM and CAD applications.

The name **bimwright** combines **BIM** with **wright**, an old word for a maker or builder—as in *shipwright*.

See [how the gateway names are chosen](https://github.com/bimwright/.github/blob/master/profile/README.md#naming).

- [**rvt-mcp**](https://github.com/bimwright/rvt-mcp) — Autodesk® Revit®
- [**dwg-mcp**](https://github.com/bimwright/dwg-mcp) — Autodesk® AutoCAD®
- [**nwd-mcp**](https://github.com/bimwright/nwd-mcp) — Autodesk® Navisworks®
- [**ipt-mcp**](https://github.com/bimwright/ipt-mcp) — Autodesk® Inventor®
- [**bim-wiki**](https://github.com/bimwright/bim-wiki) — Vietnamese-first BIM knowledge base

---

## License

Apache-2.0 — [LICENSE](LICENSE).

Forks and rebrands are welcome — the license terms are all that's required (keep `LICENSE` and the copyright notices, mark changed files). If rvt-mcp helped you, a star or a mention of BIMwright in your product is appreciated but entirely optional. Issues and PRs are always welcome.

Revit and Autodesk are trademarks of Autodesk, Inc. bimwright is an independent open-source project and is not affiliated with, sponsored by, or endorsed by Autodesk, Inc.
