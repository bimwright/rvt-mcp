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
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-237%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="https://github.com/bimwright/rvt-mcp/releases/latest"><img src="https://img.shields.io/github/v/release/bimwright/rvt-mcp" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  English · <a href="README.vi.md">Tiếng Việt</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>
</p>

---

## What it is

**An early all-C# MCP implementation for Revit — researched and implemented since March 2026.**

`rvt-mcp` is a **local** bridge between an MCP client and a running Revit session. A .NET 8 server talks MCP over stdio; a thin add-in per Revit year (2022–2027) runs inside Revit and is reached over localhost TCP (≤2024) or a named pipe (≥2025). Nothing leaves the machine, it is C# end to end, and lengths are millimetres at the tool boundary. Details: [ARCHITECTURE.md](ARCHITECTURE.md).

Agents get a **typed tool surface** for common Revit work, a C# escape hatch for everything else, and an **optional** way to turn repeated patterns into personal tools (ToolBaker): start from a shared runtime and grow *your* tools on top. Family Editor authoring is out of scope for now.

---

## Install

**v1.2.0:** [Setup and Claude Desktop MCPB](https://github.com/bimwright/rvt-mcp/releases/tag/v1.2.0) · [NuGet (server only)](https://www.nuget.org/packages/RvtMcp.Server/1.2.0) · [MCP Registry](https://registry.modelcontextprotocol.io/v0.1/servers/io.github.bimwright%2Frvt-mcp/versions/1.2.0). See the [validation record and remaining coverage](docs/testing/2026-10-18-v1.2.0-validation.md).

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

**Upgrade:** close Revit and gateway clients, back up the product data folder, then run the new installer in place without uninstalling first. Settings and ToolBaker data are kept. MCPB users use `-Client none`, update Setup and extension from the same release, then check Desktop settings and permissions before reconnecting. See [the upgrade sequence](docs/install.md#upgrade). **Uninstall:** `uninstall.ps1 -Yes` removes add-ins and the server; personal data is kept unless you add `-Purge`. To remove client entries too, first run `install.ps1 -Uninstall -Client auto`.

**Upgrading from v0.8.1 or v0.6.x:** those versions keep your settings and ToolBaker data in `%LOCALAPPDATA%\RvtMcp\`; v1.0.0 uses `%LOCALAPPDATA%\Bimwright\rvt-mcp\`, and the installer moves the old folder there. Close Revit and every MCP client first, because a running old server locks the folder. Check in PowerShell with `Test-Path "$env:LOCALAPPDATA\RvtMcp"`. If the installer stops with `Both … exist` or `Could not move …`, it has undone what it moved: keep the folder that holds your settings and ToolBaker data, **rename the other one (for example to `RvtMcp.bak`) instead of deleting it**, close the clients that were using it, and run the installer again.

**Claude Desktop extension (MCPB, optional):** direct config above is the default for agent-assisted installs. If you want the extension/settings UI, use Setup with `-Client none`, then install the `.mcpb` through Desktop's extension UI. **Choose one route, not both.** `-Client none` does not remove an existing manual registration. Use the bundle from the same selected release as Setup; it does not install the gateway/add-ins and requires that release's exact server build. Check the release notes for its signing status. See [MCPB installation and settings](docs/install.md#claude-desktop-mcpb).

---

## Videos

Community videos of rvt-mcp at work. Install steps shown in a video can be older than this README — follow the steps above.

- [Connecting ChatGPT Astra to Revit | Testing AI Changes in a Real Project](https://www.youtube.com/watch?v=J-i057B-3dI) — Revit Mentor
- [Exploring Revit + GPT 6 Astra](https://www.youtube.com/watch?v=2_W1uLn6s_I) — BIM Pure
- [GPT-6 Astra Built a Revit House in 14 Minutes](https://www.youtube.com/watch?v=pkFnSQ9Bapg) — Archi Vlogs

---

## Tools

Counts below match the **v1.2.0 release**; later development builds can expose a different catalog.

| Mode | Tools | Notes |
|------|------:|-------|
| Fresh install | **237** | `install.ps1` seeds `"toolsets": ["all"]` in `rvtmcp.config.json` |
| Bare `rvt-mcp.exe` | **50** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **237** | Full catalog |
| `all` + adaptive bake | **240** | Adds 3 suggestion-lifecycle tools |

Counts exclude your personal baked tools. The installer writes the seeded default only when `rvtmcp.config.json` doesn't already set `toolsets` — your own list survives upgrades, and removing the key (or setting your own CSV) returns a bare server to the 50-tool surface. Read-only filters individual tools by `ReadOnly=true`, including reads inside mixed toolsets. Tools that can write files are excluded even when their default output is inline.

| Toolset | What it covers |
|---------|----------------|
| `query` | View, selection, filters, stats, parameters, relationships, worksets, groups/assemblies |
| `create` | Grids, levels, rooms, line/point/surface-based elements, groups |
| `view` | Create views, sheets layout helpers, capture image, crop/scale |
| `meta` | Batch execute (max 20), multi-Revit targets, recent models, project info, purge unused (MVP), message, send_code, local failure records and operation status |
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

### Session health

At session start, select the intended Revit instance if needed, then call `revit_health()`. It stays offline by default and works with `--read-only` and no open model. `revit_health(check_updates=true)` explicitly compares the loaded gateway and add-in against one official stable release; missing evidence remains `unknown`. Follow `update_guidance`; only `prepare_update` with `bundle_update_proposal_allowed=true` permits an update proposal. Health never installs, closes models or restarts processes. It can reply while Revit is modal. Document fields are the last UI observation, dated by `document_observed_utc`, and do not confirm the current document or readiness for writes. After reconnect, compare fresh `initialize.serverInfo` and the health tool runtime metadata/schema/annotations in `tools/list` with `client_verification`; the server cannot observe client cache refresh.

### Argument errors

A call missing a required parameter does not run. It returns `INVALID_ARGUMENT` with `invalid_arguments` (`missing`, `unknown` with `did_you_mean`, `received`, `expected`). Parameter names are case-sensitive, and some tools use snake_case while others use camelCase: follow `did_you_mean`. A key that is not a parameter does not stop the call. It is ignored, and the result lists it under `_ignored_arguments`. A value that cannot be converted still returns `An error occurred invoking '<tool>'.`, with the exact cause in the server's stderr.

### send_code, ToolBaker, ribbon and languages

- **`revit_send_code_to_revit`** (on by default) compiles and runs a C# body inside Revit when no typed tool fits; `--read-only` or `--disable-send-code` removes it. See [docs/send-code.md](docs/send-code.md), and [docs/stairs-workflow.md](docs/stairs-workflow.md) for stairs.
- **ToolBaker:** `revit_list_baked_tools` / `revit_run_baked_tool` need `--toolsets toolbaker`. Adaptive bake (`--enable-adaptive-bake`, off by default) suggests tools from repeated calls; nothing is added until you accept one. Bake compiles inside Revit — no Visual Studio needed. See [docs/bake.md](docs/bake.md).
- **Ribbon:** start or stop the connection, open **History** to search past calls and review **Issues**, and switch completion **Toasts** (on by default).
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

**v1.2.1 source (release preparation):** Historical Issues no longer block later writes or identical requests. Current transaction warnings still default to rollback; an explicit `accept-and-log` warning policy is available in **Settings → General → Safety**, JSON, environment or CLI. Error, corruption, incomplete capture and current persistence failures still require rollback. Review records in **History → Issues**; `revit_get_failure_records` and `revit_get_operation_status` read local state even while Revit is modal. Inspect pending/unknown outcomes before deciding what to do next. See [controlled failures and acceptance limits](docs/controlled-failures.md).

Since v1.0.0 the server reports `_changes` and local per-model `_history`. History defaults on independently of call logs; use `--disable-change-history` to disable recording. The `meta` tools `revit_record_change`, `revit_get_change_records` and `revit_resolve_history_identity` attach reasons to explicit call IDs, query stored changes and record the owner's history choice after a copy or Save As. See [change tracking](docs/change-tracking.md) for privacy, limits and recovery.

`revit_survey_change_impact` is a tool in `query`: a bounded, read-only survey of ten relationship groups, including with send-code disabled. `scopeThreshold` is required for each request; view/schedule iteration is opt-in through `maxViews > 0`. Partial results remain explicit and parameter observations are not trusted history snapshots. See [change-impact survey](docs/change-survey.md).

Precedence, high wins: **CLI → env (`BIMWRIGHT_*`) →** `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvtmcp.config.json`.

| Setting | CLI | Env | JSON |
|---------|-----|-----|------|
| Target selector | `--target 2024` / `--target pid:12345` | `BIMWRIGHT_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `BIMWRIGHT_TOOLSETS` | `toolsets` |
| Read-only | `--read-only` | `BIMWRIGHT_READ_ONLY=1` | `readOnly` |
| send_code | `--enable-send-code` / `--disable-send-code` | `BIMWRIGHT_ENABLE_SEND_CODE` | `enableSendCode` |
| Call log | `--enable-call-log` / `--disable-call-log` | `BIMWRIGHT_ENABLE_CALL_LOG` | `enableCallLog` |
| Warning policy (default `rollback`) | `--warning-policy rollback\|accept-and-log` | `BIMWRIGHT_WARNING_POLICY` | `warningPolicy` |
| Change history (default ON) | `--enable-change-history` / `--disable-change-history` | `BIMWRIGHT_ENABLE_CHANGE_HISTORY` | `enableChangeHistory` |
| Response guard | `--enable-response-guard` / `--disable-response-guard` | `BIMWRIGHT_ENABLE_RESPONSE_GUARD` | `enableResponseGuard` |
| Warn bytes | `--response-warn-bytes` | `BIMWRIGHT_RESPONSE_WARN_BYTES` | `responseWarnBytes` |
| Strong warn bytes | `--response-strong-warn-bytes` | `BIMWRIGHT_RESPONSE_STRONG_WARN_BYTES` | `responseStrongWarnBytes` |
| Budget bytes | `--response-budget-bytes` | `BIMWRIGHT_RESPONSE_BUDGET_BYTES` | `responseBudgetBytes` |
| Transport cap | `--max-response-bytes` | `BIMWRIGHT_MAX_RESPONSE_BYTES` | `maxResponseBytes` |
| Spill retention (default 36 h) | `--spill-retention-hours <n>` | `BIMWRIGHT_SPILL_RETENTION_HOURS` | `spillRetentionHours` |
| LAN bind (plugin) | — | `BIMWRIGHT_ALLOW_LAN_BIND=1` | `allowLanBind` |
| Remember MCP On/Off at startup (plugin; default on) | ribbon **MCP: ON / OFF** | `BIMWRIGHT_AUTO_START_TRANSPORT=0/1` | `autoStartTransport` |
| ToolBaker surface | `--enable-toolbaker` / `--disable-toolbaker` | `BIMWRIGHT_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| Cache send_code bodies (bake clusters) | `--cache-send-code-bodies` / `--no-…` | `BIMWRIGHT_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| Persist send_code journal | `--persist-send-code-bodies` / `--no-…` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL (1h-14d) | `--persist-send-code-bodies-for 4h` | `BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| Completion toast (default on) | ribbon **Toast** | `BIMWRIGHT_ENABLE_TOAST=0` | `enableToast` |
| Toast branding (default off, saved) | Settings → Toast → **Show branding** | — | `showBranding` |
| Toast idle duration (default 20 s) | Settings → Toast → **Idle duration** | — | `toastIdleSeconds` |
| Toast position (default top-left, no drag; saved) | Settings → Toast → **Horizontal / Vertical alignment**, **Allow dragging the card**, **Reset position** | — | `toastHorizontalAlign`, `toastVerticalAlign`, `toastDragEnabled`, `toastDragOffset` |
| UI language (add-in) | ribbon **Language** | `BIMWRIGHT_UI_LANGUAGE` | `uiLanguage` |

Settings warning-policy changes use **Apply / Discard** and apply to the next MCP call without retrying earlier calls. Only an explicit client CLI/environment policy overrides the host's per-call configuration; a gateway's startup JSON does not freeze the saved Settings value. A Revit-process environment override is shown in Settings and disables that row. See [warning-policy limits and receipts](docs/controlled-failures.md#warning-policy).

Journal retention offers exactly **1h, 4h, 8h, 12h, 24h, 2d, 7d, 14d** in Settings. The saved `persistSendCodeBodiesHours` remains an integer hour count (1-336); the default remains 4h. A longer window explicitly retains journal content longer; it does not enable journaling by itself.

After changing server flags, restart the MCP connection so the client picks up the new tool list.

The MCP ribbon toggle saves its last successful On/Off state as `autoStartTransport` in the same config file. Revit uses that value on its next launch; closing Revit or stopping transport programmatically does not overwrite it. The file is shared by Revit versions and instances for the same Windows user, so the last saved toggle determines startup for future instances without changing instances already running. An unset value defaults to On. `BIMWRIGHT_AUTO_START_TRANSPORT`, when set in the **Revit process** environment, overrides the saved value at startup; the ribbon still saves the user's choice, but the environment override continues to win on later launches. A save failure is reported and the toggle applies only to the current session.

If another third-party MCP add-in is detected for that Revit version, rvt-mcp starts **Off**, even when the saved state or environment requests On. Autodesk's official Revit MCP is exempt. This startup override preserves the saved preference. Clicking the ribbon toggle to start manually shows a localized modal notice with the number of third-party MCP add-ins, including rvt-mcp; acknowledging it allows startup. Detection reads `.addin` manifests and compatible bundle registrations, recognizing MCP names/classes/assembly names and deduplicating registrations. MCP implementations hidden behind generic loader metadata may not be recognized.

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
      "mcp__rvt-mcp__revit_get_failure_records",
      "mcp__rvt-mcp__revit_get_family_instances",
      "mcp__rvt-mcp__revit_get_group_members",
      "mcp__rvt-mcp__revit_get_link_coordinate_system",
      "mcp__rvt-mcp__revit_get_link_elements",
      "mcp__rvt-mcp__revit_get_material_properties",
      "mcp__rvt-mcp__revit_get_material_quantities",
      "mcp__rvt-mcp__revit_get_mep_element_connectors",
      "mcp__rvt-mcp__revit_get_model_warnings_summary",
      "mcp__rvt-mcp__revit_get_operation_status",
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
      "mcp__rvt-mcp__revit_health",
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
      "mcp__rvt-mcp__revit_lookup_message",
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

send_code defaults **on**, independently of ToolBaker; call-log defaults **off**. CLI overrides environment variables, which override JSON. Authenticated server settings override plugin settings for that request only. Call-log off suppresses the server journal, plugin `mcp-calls.jsonl` and send-code body journal; in-memory History remains available. Body journaling needs both call-log on and its separate TTL opt-in. ToolBaker `usage.jsonl` is separate and follows adaptive-bake settings.

Diagnostics from the gateway and Revit add-ins share `%LOCALAPPDATA%\Bimwright\rvt-mcp\rvt-mcp.log`: 10 MiB plus one 10 MiB backup. Entries include UTC date, process ID and Revit year; repeated messages are summarized. Logging uses a bounded background queue, so a locked file or slow disk can drop diagnostics without blocking Revit. Known legacy `revit-mcp.log`, `debug.log` and `baked/compile-refs.log` files retain only a 64 KiB tail; they are no longer copied during runtime data migration.

Call logs, daily journals, send-code journals, usage and bake audit records each retain at most **30 days / 100 MiB per group**. New files rotate before an append exceeds 5 MiB and at UTC day rollover; startup maintenance and subsequent writes remove the oldest generated archives when needed. Usage analysis still covers at most 21 days and 5,000 recent records. Send-code body TTL remains an additional, shorter limit. Maintenance recognizes only generated log names and does not follow linked files or folders; models, configuration and databases are outside its scope.

The response guard defaults **on**: UTF-8 warnings at 65536 bytes, strong warnings above 262144, a 716800-byte budget and a 1048576-byte transport cap. JSON escaping and MCP content/metadata are measured at the server. Oversized reads return `RESPONSE_TOO_LARGE` with narrowing guidance; completed writes return a compact summary. Arbitrary code output spills to a local file with `mutation_applied: null`; inspect the file instead of rerunning the command. A spill file is kept 36 hours by default (`--spill-retention-hours`, 1-8760; an invalid value uses 36) and no file-count cap deletes a younger file. Turning the guard off leaves the transport cap active. Byte limits must be integers >=1024, ordered `warn <= strong <= budget <= max`; adjust lower thresholds when lowering the budget.

## Supported Revit versions

| Revit | Plugin TFM | Transport |
|-------|------------|-----------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

Full Revit desktop only; Revit Viewer is not a supported target. CI builds all six add-ins, but runtime depth varies by year — recheck baked tools and custom C# on the years you use.

---

## Multiple Revit instances

The server binds to exactly one Revit instance — including when several instances of the same year run at once. A query can bind automatically only when exactly one instance matches. Multiple matches return `AMBIGUOUS_TARGET`; model/file writes require a selected target and model writes also require a verified document. Selectors: `auto`, a 4-digit year (`2024`), `pid:<n>` or `id:revit-<year>-<pid>`; legacy R-codes like `R24` are rejected.

When more than one instance runs — especially several of the same year — do not guess the target. In writable sessions:

1. Call `revit_list_available_targets`: every live instance is listed with `target_id`, `pid`, `host_year`, `window_title` and `source`.
2. Pick the instance by `pid` and/or `window_title`.
3. Select the process with `revit_switch_target(version="pid:<n>", verify=false)`, query its active view/document, then confirm with `revit_switch_target(version="pid:<n>", verify=true, expectedDocumentId="<document_id>")`. Copy the identity from the query's `_target` stamp; omitting `expectedDocumentId` confirms the document reported by the verify probe. `auto`/year selectors are accepted only with one matching instance.
4. `revit_get_current_target` shows the binding, generation, `selection_confirmed`, `confirmed_document_id` and `confirmation_required`. Successful host responses carry `_target` with the actual PID, year and document identity, including after response compaction.

Leaving a live selected instance requires `rebind=true`. Every accepted switch advances the generation so earlier calls cannot adopt a later confirmation. Failed verification restores the previous live binding if no newer switch intervened. A closed/reopened document, another project or Edit Family has a different document identity; the add-in checks the expected identity on the Revit UI thread before executing each write. Reading a different document never changes the write pin. Review and confirm that document before writing. `open_model` establishes a document: select the process with `verify=false`, open the model, then verify it. It cannot run inside `batch_execute`.

Target error codes and recovery in writable sessions. Read-only sessions use the startup/restart route below. Every code except `TARGET_INTERRUPTED` means the command was **not** sent:

| Code | Meaning | Agent action |
|---|---|---|
| `NO_TARGET` | No live instance matches the selector (also: the implicit binding was lost and zero candidates remain) | Call `revit_list_available_targets`; start Revit / the add-in or fix the selector |
| `TARGET_UNAVAILABLE` | The bound instance is still alive but cannot be reached — listener stopped or restarting, descriptor missing, token rejected. The binding is kept | Retry shortly; ask the user to (re)start MCP in that Revit. Do not switch to another instance unless the user wants that |
| `TARGET_BUSY` | The endpoint exists but did not complete the handshake within 5 s — usually another agent or gateway holds its single connection, or Revit is blocked | Retry later; do not run two gateways on one instance |
| `TARGET_LOST` | The explicitly pinned (`pid:`/`id:`) instance exited | Check `candidates`, confirm with the user, then `revit_switch_target` |
| `TARGET_CHANGED` | The implicitly bound instance exited and exactly one replacement exists (or the binding changed between call arrival and send). The command was **not** sent; every call returns this until confirmed | Check the proposed instance (`window_title`), confirm with `revit_switch_target`, then repeat the call |
| `AMBIGUOUS_TARGET` | First selection or a switch matches multiple instances | List, pick by `pid`/`window_title`, then use `pid:`/`id:` |
| `TARGET_UNCONFIRMED` | The selected target/document has not been confirmed for writes | Verify the intended PID and document with `revit_switch_target` |
| `TARGET_REBIND_REQUIRED` | A live selected target would be replaced | Review the binding, then switch with `rebind=true` if intended |
| `TARGET_INTERRUPTED` | The command was sent but the connection closed before a response (`sent:true`, `outcome:unknown`) | Inspect the model state before repeating any write |
| `INVALID_TARGET_SELECTOR` | Malformed selector or R-code | Use `auto`, a year, `pid:<n>` or `id:<target_id>` |

**After a Revit restart — even with a single Revit open** — a writable session returns `TARGET_CHANGED` and waits for one `revit_switch_target` call before sending anything. The restarted Revit can have a different model open; review the proposed instance, confirm it and repeat the call.

**Read-only sessions:** call `revit_list_available_targets`, choose `--target pid:<n>` at gateway startup and restart the gateway whenever the target changes. `revit_switch_target` is unavailable; the target errors above provide startup/restart guidance instead of recommending that tool.

`revit_switch_target` changes session routing and is excluded from `--read-only`, matching its non-read-only annotation. Read-only mode exposes 98 tools with all toolsets. Select the intended process using `--read-only --target pid:<n>` at gateway startup; to change it, restart the gateway with the new selector. `revit_list_available_targets` and `revit_get_current_target` remain available for inspection.

`DOCUMENT_UNCONFIRMED` and `DOCUMENT_CHANGED` are add-in refusals: the request reached Revit but the model operation did not start. Check the reported `_target`, then confirm the intended document. Gateway and add-ins must both support `document_target_v1`; update them together. An add-in connected to an older gateway returns `PAIRING_REQUIRED` instead: update the server, because that gateway cannot confirm a document.

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
| [docs/controlled-failures.md](docs/controlled-failures.md) | Warning policy, advisory Issues, native decisions and pending-operation recovery |
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

## Validation

See the [tool descriptor benchmark](docs/benchmarks/2026-10-18-tool-descriptors.md) and [v1.2.0 validation record](docs/testing/2026-10-18-v1.2.0-validation.md). They distinguish affected regression results, earlier native evidence and pending release coverage; descriptor bytes do not measure model tokens or native performance.

## License

Apache-2.0 — [LICENSE](LICENSE).

Forks and rebrands are welcome — the license terms are all that's required (keep `LICENSE` and the copyright notices, mark changed files). If rvt-mcp helped you, a star or a mention of BIMwright in your product is appreciated but entirely optional. Issues and PRs are always welcome.

Revit and Autodesk are trademarks of Autodesk, Inc. bimwright is an independent open-source project and is not affiliated with, sponsored by, or endorsed by Autodesk, Inc.
