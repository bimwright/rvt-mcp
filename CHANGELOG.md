# Changelog

## Release history

| Version | Date | Available as |
|---|---|---|
| v1.0.0 | Pending | Release candidate; not yet published |
| v0.8.1 | 2026-09-27 | [GitHub Release](https://github.com/bimwright/rvt-mcp/releases/tag/v0.8.1) |
| v0.6.3 | 2026-09-25 | [GitHub Release](https://github.com/bimwright/rvt-mcp/releases/tag/v0.6.3) |
| v0.6.2 | 2026-09-22 | [GitHub Release](https://github.com/bimwright/rvt-mcp/releases/tag/v0.6.2); NuGet `RvtMcp.Server` 0.6.2 |
| v0.6.1 | 2026-08-28 | [GitHub Release](https://github.com/bimwright/rvt-mcp/releases/tag/v0.6.1); NuGet `RvtMcp.Server` 0.6.1 |
| v0.6.0 | — | Not published on its own; shipped inside v0.6.1 |
| v0.5.0 | 2026-05-22 | Git tag |
| v0.4.0 | 2026-05-21 | Git tag |
| v0.3.1 | 2026-05-18 | Git tag |
| v0.3.0 | 2026-04-27 | Git tag |
| v0.2.1 | 2026-04-24 | Git tag |
| v0.2.0 | 2026-04-21 | Git tag |
| v0.1.2 | 2026-04-19 | Git tag |
| v0.1.1 | 2026-04-19 | Git tag |
| v0.1.0 | 2026-04-17 | Git tag (public launch) |

Install only the [latest GitHub Release](https://github.com/bimwright/rvt-mcp/releases/latest). v0.1.0–v0.5.0 are kept as git tags for history; any GitHub Releases for them are no longer published, and the legacy NuGet package `Bimwright.Rvt.Server` (0.1–0.3) is obsolete.

## v1.0.0 - Change tracking, runtime controls, and viewport layout

Follows v0.8.1. Tool counts: default **46**, `--toolsets all` **233**, adaptive bake **236**, `--read-only` **95** (without personal baked tools). Server and add-ins must come from the same release.

### Added

- **`revit_change` prompt** — surveys existing relationships, agrees a scope for each request, asks for confirmation of a concrete proposal before writing, then reads back the targets and records the user's reason in the conversation. Requires `query,meta`, not `send_code`; read-only sessions stop at survey/proposal. Missing or incomplete checks remain explicit, and readback does not claim exhaustive changed-element tracking. Adds a server-instructions pointer. v1.0.0 offers six prompts in total; there is no persistent change database or server-enforced workflow lock.

- **Typed change-impact survey** — `revit_survey_change_impact` in `query` reports ten bounded relationship groups with explicit incomplete coverage, exact/lower-bound counts and discussion flags. Works with read-only and send-code disabled. Each request needs `scopeThreshold`; view/schedule iteration defaults to off and requires `maxViews > 0`. Native calls cannot be preempted by the soft time budget. No persistent survey/history snapshot is created. `revit_change` now uses this tool.

- **MCP-scoped element changes** — `DocumentChanged` capture adds `_changes` for committed additions, modifications and deletions, including indirect/internal elements. Responses group IDs by document and cap each list at 200 while retaining counts. Ordinary reads remain unchanged. Known batch rollback returns no changes; arbitrary group rollback reports incomplete coverage rather than guessing. Paths are not collected, and the optional call journal stores `Changes` separately from truncated result text. No persistent reason database is introduced.

- **Local change history** — per-model SQLite stores captured MCP calls and all tracked IDs separately from the public 200-ID cap. `revit_record_change` binds the user's reason to explicit server-issued call IDs; `revit_get_change_records` queries IDs, UniqueIds and dates with unknown/unassigned reasons visible. History defaults on independently of call logs and has JSON/env/CLI off switches. Private model identities stay local; pending captures support idempotent recovery after server/storage failures. Capture bounds, unavailable identities and storage failures remain explicit. No Save/Sync or later manual Undo tracking is claimed.

- **History identity continuity** — proven Windows file aliases and renames can follow the same history. Revit lineage only suggests a choice for copies/Save As; `revit_resolve_history_identity` records the owner's explicit continuation or independent history. Existing databases and reason ownership stay intact, links are reversible, and legacy keys can be listed with `revit_get_change_records(listModels=true)`. Ordinary reads remain unchanged.

- **Survey-to-history observations** — an enabled typed survey retains only a bounded, session/document-bound RAM selection for five minutes and one write. UI changes/Undo, view/document transitions and Save As invalidate it. The plugin remeasures four selected built-ins at execution and stores observations only for captured modified elements with matching UniqueIds. Existing typed-setter values remain intact. Stale/missing coverage is explicit; public survey JSON cannot supply measured before-values. Read calls still create no history/pending file. Legacy databases remain readable without a write-time migration on reads.

- **Viewport layout in sheet space** — `revit_get_viewport_geometry` reports each viewport's visible crop rectangle, view outline, box centre, scale, rotation, pinned and crop flags in sheet millimetres, with `coordinate_frame` stating that these are paper coordinates, not project coordinates; annotation hanging past the crop is flagged. `revit_align_viewports` aligns viewports to a reference viewport (`left`, `right`, `top`, `bottom`, `center_x`, `center_y`, `center`) or centres a group on the title block extent (`center_on_sheet`); `dryRun` defaults to true, pinned or rotated viewports are refused before anything moves, and the result reads back the applied positions. One Revit Undo step. On Revit 2026 and later a viewport bound to a saved position is refused as well (Revit puts it back at regeneration, so a committed move would not hold); `revit_get_viewport_geometry` reports it as `saved_position`. The result carries a `status` of `applied`, `partially_applied` or `not_applied` judged on the read-back, with `failed_viewports` for moves Revit did not keep; when none held the call fails instead of reporting `applied`. The new `revit_drawing_layout` prompt walks through reading, agreeing the alignment, a dry-run plan, confirmation and read-back. Both tools live in the `sheets` toolset.

- **Runtime switches** — `--enable-send-code` / `--disable-send-code` control send_code independently of ToolBaker (default on). `--enable-call-log` / `--disable-call-log` control server and plugin call persistence (default off); send-code body journaling additionally requires its TTL opt-in. Authenticated server settings apply per request without changing the plugin's saved configuration.

- **Per-tool read-only permissions** — all typed tools explicitly declare the four MCP hints; send_code carries no annotations. `--read-only` exposes the 95 read-only tools within the selected toolsets, excluding document/file writes while retaining transient UI operations. The four README allow lists are checked against the annotations. Arbitrary code and baked tools must be invoked directly, outside `batch_execute`.

- **Configurable response guard** — on by default, with CLI/environment/JSON controls for warning, strong-warning and response budgets. A server filter measures UTF-8 MCP result bytes including escaping and metadata; the transport cap remains active when the optional guard is off. Oversized reads fail with narrowing guidance, completed writes return compact outcomes, and arbitrary output spills once to a local file with an unknown mutation outcome. Rechecking an existing Unicode spill preview preserves the original artifact. Spill files are kept 36 hours by default (v0.8.1: 24 hours and at most 50 files); `--spill-retention-hours`, `BIMWRIGHT_SPILL_RETENTION_HOURS` or `spillRetentionHours` change it, an invalid value uses 36, and no file-count cap deletes a file younger than the retention time.

- **`revit_list_recent_models`** — lists this Revit's recent files when no document is open. The agent asks which one to open, then calls `revit_open_model`. A no-document failure now says Revit is running and names that tool.

- **Tool search** — Settings → Tools has a search box that matches the tool name or its description. Each row tooltip shows the name and the full description.

- **Toast position and richer tooltips** — Settings → Toast has independent horizontal (Left/Right) and vertical (Top/Bottom) alignment for the four corners, an opt-in **Allow dragging the card** switch that moves the card by its title row, and **Reset position**. Changes apply to the open card and are saved immediately; changing a corner clears the saved drag offset, turning drag off keeps it, and a plain click still opens History. Offsets are stored relative to the Revit window in DIPs and clamped to the work area of its monitor. A bottom-anchored card grows upward, with an opposite-side fallback when there is no room; the card enters and leaves from its own side, and corner changes glide. Motion follows the Windows animation setting, and a failed save is shown under the setting while the choice stays active for the session. A new result in an open timeline now glides in: the rows rise by the distance the list scrolled, the new row fades in and its outcome dot pops, without changing the card size or the scroll offset. A reader who has scrolled up keeps their place with no effect (the scroll bar only hints that the list grew), and reduced motion shows the row directly. The latest-tool line and timeline rows now show the sanitized result, Success/Failed, completion time and measured duration on hover. Strings for all 15 languages. The default is still top-left with no drag, but a top-corner card now sits 56 DIPs lower so it clears Revit's title bar and ribbon tabs (bottom corners are unchanged); coordination between toasts of several Autodesk applications is not included.

- **Toast activity timeline** — deliberate hover reveals the latest three compact outcomes with completion times and distinct success/failure nodes; scroll to older calls within the same card. New results follow the tail while keeping an older reading position stable. Entries stay in RAM with bounded, redacted summaries; script objects/arrays show field/item counts and surveys disclose incomplete coverage. First movement over a card that appeared under a stationary cursor now pauses idle, and late hover drains its closing render. History clicks, localization, capture hold, branding and reduced motion remain supported.

- **Toast card layout** — the title names the gateway and Revit year (`rvt-mcp 2022`), replacing the small footer label and the `RVT-MCP` prefix. The next line names the latest tool, and the Success · Failed · Capture counts spread evenly across the card, each number sized and aligned with its label.

- **Capture thumbnail stays at least 5 seconds** — a later result without an image clears the preview only after that. Back-to-back captures switch to the newest one every 5 seconds. The preview does not change while the pointer is on the card.

- **Toast motion** — counters roll like an odometer: only the digits that change move, a new value continues the roll instead of restarting it, and a number that gains a digit widens gradually. The capture sits in one fixed frame with the image centred on both axes, so the card keeps its size between captures of different shapes. The frame opens with the card growing around it, cross-fades to the next capture, and fades out before the card closes up. All of it follows Windows' animation setting: with **Animation effects** off, everything changes at once.

- **Persistent branding switch** — Settings → Toast → **Show branding** (off by default). The wordmark stays hidden until the pointer moves onto the card, wipes in, and fades out when the pointer leaves. While branding is on, the card keeps that bottom row so the height does not change on hover. The card is narrower. A stationary cursor under a card that just opened does not count. The choice applies immediately and is saved as `showBranding` in the user config, including when turned off. A save failure is reported beneath the switch; the new state still applies to the current session.

- **Update notice** — the Revit add-in asks `https://api.github.com/repos/bimwright/rvt-mcp/releases/latest` in the background at startup, at most once every 24 h (5 s timeout, errors ignored, prereleases ignored), and caches the answer in `%LOCALAPPDATA%\Bimwright\rvt-mcp\update-check.json`. This is the only outbound request the add-in makes by itself. When a newer stable release exists, a small card without focus steal appears at the bottom right with **Copy prompt** (a ready-to-paste prompt for your AI agent with the installed version, the latest version and the release URL), **Open release page**, **Skip this version** (remembered) and **Close**. Set `BIMWRIGHT_DISABLE_UPDATE_CHECK=1` to turn the check off. The server never calls the network for this: when the cached check knows a newer, not-skipped release, the first line of the `initialize` instructions says so (the 2 KB cap still holds) and `revit_list_available_targets` / `revit_get_current_target` carry an `update_available` object (`null` when up to date). Strings for all 15 languages.

### Changed

- **Data folder moved to `%LOCALAPPDATA%\Bimwright\rvt-mcp\`** — all per-user state (config, discovery, logs, captures, ToolBaker data, locales) and the server (`server\current\rvt-mcp.exe`) now live under the shared bimwright family root. The installer moves an existing `%LOCALAPPDATA%\RvtMcp\` once, byte-identical (Revit and MCP clients using rvt-mcp must be closed), repoints known client entries to the new `server\current` path, restores the old folder if the install fails, and stops when both folders exist. Server and add-ins must come from the same release.

- **Destructive hints and response outcomes** — tools that replace or delete existing data now advertise `Destructive=true` with recovery guidance, including rebindings and takeoff-file overwrites. Repeated plugin/server response compaction preserves dry-run, rollback and unknown mutation outcomes. Takeoff exports through `output_path` retain successful completion when response detail is oversized; failed exports remain errors. Regression tests cover permission semantics, the two-stage guard and shared-config test isolation.

### Fixed

- **Untyped JSON arguments** — preserve JSON values bound by the MCP SDK inside arrays and nested objects, including dimension references, filled-region and room-separator points, sheet renumber items, volumes and baked-tool arguments.

- **Room and area failure handling** — capture Revit warnings in the tool result and silently roll back unresolved errors instead of opening failure dialogs. A rolled-back room transaction returns an error.

- **MEP disconnect filter description** — advertise `mechanical`, matching the handler validation.

- **SQLite spill initialization in Revit** — initialize the referenced SQLite bundle explicitly when writing a database, so another add-in's loaded bundle cannot leave this gateway's provider unset.

- **Structured send-code toast results** — object and array results now produce compact JSON summaries instead of failing toast completion. Scalar/null handling and the 100-character summary limit are preserved.

- **Closed-document change capture** — capture summaries use metadata collected during transactions, so a command that closes its temporary project or EditFamily document before returning does not dereference an invalid Revit document.

- **History review fixes** — ordinary reads no longer publish pending captures or add `_history`. Unsaved/unresolved documents are skipped before writing; legacy invalid captures are quarantined and bounded recovery runs in the background. `revit_get_change_records` can resolve the active saved model without a receipt from a read. Server options cannot relax plugin read-only/send-code policy. Runtime data migration now covers old product folders, resumes partial copies, preserves SQLite WAL data and reports conflicts before loading fallback settings. History failures emit redacted diagnostics; Toast tests use isolated temporary folders.

- **ToolBaker on Revit 2027** — compiler policy now respects type accessibility, matching normal C# compilation. Internal global Revit types no longer shadow public API names such as `UIApplication` and `Transaction`; the API allowlist and blocked operations are unchanged.

- **Installer wires Claude Code on Windows PowerShell 5.1** — client CLI calls (`claude`, `codex`, `grok`) no longer run under `$ErrorActionPreference = 'Stop'`, so a harmless stderr line such as `No MCP server named "rvt-mcp" in local scope` on a fresh machine no longer fails the wiring (it was reported as `claude: failed` while the install exited 0). Regression test added.

### Documentation

- **Why activity toasts exist** — the English and Vietnamese READMEs explain tool-result feedback, freedom from watching the chat, multitasking and a more modern, non-modal experience.

## v0.8.1 - Two-sided installer, revit_open_model, and a Settings window

Follows v0.6.3; there are no v0.6.4–v0.8.0 releases.

- **One installer run sets up both sides** — the add-in for every installed Revit 2022–2027 and the `rvt-mcp` entry in every MCP client it detects.
- **New tool `revit_open_model`** and four **MCP prompts**.
- **Part of community PR [#15](https://github.com/bimwright/rvt-mcp/pull/15) merged** — its `send_code` and `switch_target` fixes are cherry-picked, and its `revit_open_model` proposal was reworked to follow the workshared-model policy. Thanks [@PhanCongVuDuc](https://github.com/PhanCongVuDuc).
- Tool counts: default **41**, `--toolsets all` **226**, adaptive bake **229**.

### Installer

- **`-Client` installer wiring** — every install wires each detected client (`-Client <names>` limits it, `-Client none` skips it; `-Uninstall` only unwires clients named with `-Client`) and applies the `docs/mcp-client-wiring.md` procedure for 19 supported clients: Claude Code/Desktop, Codex, Grok CLIs, and file-config clients (Cursor, VS Code, Cline/Roo, Zed, Gemini, Antigravity, Devin, LM Studio, kun, Kiro, Qwen, Windsurf, OpenCode, Kilo). File edits are minimal text surgery — JSONC comments and sibling entries survive, a `.bak` backup is written first, and a failed write restores it. Versioned-path entries are repointed to `current` with args/env kept; custom launchers and legacy `bimwright-rvt*` entries are reported, never replaced. A config whose keys differ only in case (Claude Code's per-project Windows paths) is still wired instead of being reported as invalid JSON. Cherry Studio gets a `cherrystudio://` deeplink. `-Uninstall -Client <names>` removes just the `rvt-mcp` entry, and `-WireClient` remains as a deprecated alias.
- **Full tool surface on install** — the installer seeds `%LOCALAPPDATA%\RvtMcp\rvtmcp.config.json` with `"toolsets": ["all"]` when the file doesn't already set `toolsets`, so fresh installs expose all 226 tools. An explicit `toolsets` choice is preserved on upgrade; `--toolsets`, `BIMWRIGHT_TOOLSETS` and `--read-only` still outrank the file, and a bare `rvt-mcp.exe` without it still defaults to `query,create,view,meta`.
- **Claude Desktop on MSIX installs** — `-Client` probes `Packages\*\LocalCache\Roaming\Claude` for any package family name (not just `Claude_*`) and prefers a package that actually contains the Claude cache over a stray `%APPDATA%\Claude` dir, which an MSIX install never reads. Wiring while `claude.exe` is running emits a warning that the app rewrites the config from memory on quit; `cowork*` keys and other app state are preserved.
- **Self-wiring guidance** — `docs/mcp-client-wiring.md` now covers the agent that configures the client it runs inside: wire last (restart ends the session), the running app can overwrite the config on exit, and `tools/list` verification happens in the next session.

### Added

- **`revit_open_model`** (`meta` toolset) — opens a `.rvt`, `.rte` or `.rfa` and makes it active, or opens it in the background. A file whose header cannot be read is never opened, because its worksharing state is unknown. A workshared `.rvt` is never opened directly: a new local copy is always created from its central model (same as Revit's Open → "Create New Local") under the Revit.ini `ProjectPath` folder as `<central>_<username>.rvt` — missing `ProjectPath` stops the call rather than picking a folder. An existing local at that path is renamed with a timestamp — never deleted or overwritten — unless it is open in the session, has changes not yet saved to central, or its header cannot be read, in which case the call stops and asks the user. `worksets` (all|none|lastViewed) applies to workshared models; `audit` is opt-in because it is slow; there is no `detach` option — detaching or opening a central directly goes through `revit_send_code_to_revit`. A model Revit already has open is reported back (and activated when `activate=true`) instead of reopened. `saved_in_version` shows when an older file is being upgraded in memory. If `CreateNewLocal` refuses after the existing local was renamed aside (e.g. a central saved in an older Revit version), the error names the timestamped file it was kept as. A file currently loaded as a link in an open document is refused with a message to close the host or unload the link first.
- **Long-run `timeout_seconds`** on `revit_open_model`, `revit_link_revit_model`, `revit_reload_link` and `revit_load_family_from_path` — the request envelope carries the caller's plugin wait (1–900 s, default 600 s; out-of-range values are refused rather than clamped) because opening a model that pulls in many links commonly exceeds the 60 s default. Other tools keep the fixed 60 s wait.
- **MCP prompts** — `revit_getting_started`, `revit_model_audit`, `revit_pre_issue_check`, `revit_stairs`: user-invoked markdown workflows, always listed; a prompt whose toolsets aren't enabled returns the exact `--toolsets` line instead of its steps. `revit_stairs` is the one write-capable prompt (via `send_code`) and asks for confirmation before writing. Prompts never advise turning off `--read-only`; `revit_pre_issue_check` needs explicit member sheets for a named sheet set and reports unresolved or incomplete checks as `NOT VERIFIED`; `revit_stairs` carries its own transaction, failure-reporting and scope-cleanup template.
- **Settings window** — the ribbon slide-out gains **Settings** and **Language** buttons (replacing the language combo; Language opens Settings → General → Language). General shows the live connection state, Toast sets the card idle duration (10/20/30/60 s), Tools lists the connected server's tools with their timeout policy, and About carries version plus an offline license view. Each setting is one row (label and short explanation left, control right) inside flat tabs and white cards under a `BIMwright | RVT-MCP Settings` header. Connection fits two rows: a coloured state badge with listener restart (new port/pipe and token) and On/Off — the same actions as the ribbon MCP toggle — then `Transport type: TCP | Port: …` with Copy, disabled when there is no TCP port. Save errors appear under the setting they belong to; scrollbars match History. **Apply** and **Discard changes** (formerly Cancel) are enabled only while changes are unsaved; Toast On/Off and Language still apply immediately. Styles are scoped to the Settings window.

### Fixed

- **`revit_send_code_to_revit` with no document open** — the wrapper read `app.ActiveUIDocument.Document` before the snippet ran, so every call threw a bare null reference when Revit had no document, including a snippet calling `app.OpenAndActivateDocument`. `doc` and `uidoc` are now `null` in that case and the snippet decides what to do. The same null reference in the `run_baked_tool` wrapper (`ToolCompiler.cs`) is fixed as well.
- **`revit_list_available_targets` hint** — it told callers to pass a `year` to `revit_switch_target`, whose parameter is named `version`; following it produced an invocation error that read as a dead server.
- **Toast and History showed some failures as successes** — a `revit_batch_execute` that rolled back or had failed sub-commands, and a response rejected as too large (which the agent already received as `success=false`), showed green in the toast and History and were logged as successes in `mcp-calls.jsonl`. They now show as failures with the reason. The response to the agent is unchanged.
- **`dotnet build src/RvtMcp.sln` on a clean tree** — the test project's server reference now compiles into its own `obj`, so parallel builds no longer collide with the solution's server build (`MSB3371`/`CS2012`).

### Changed

- **Activity toasts use one shared card** — successive results update a single card (20-second default idle lifetime, configurable in Settings and paused while hovered) instead of stacking per-result windows. Its three rows show `RVT-MCP - {tool name}`, vertically rolling Success / Failed / Capture counts, and the right-aligned BIMwright wordmark. Success keeps the light-blue gradient, errors keep red; capture thumbnails are no longer embedded. The × button stays transparent on hover, and the brand's two letter layers crossfade within a moving highlight without dimming the whole logo. Animations respect Windows reduced-motion preferences.
- **Activity card clicks open History** — clicking the card routes to the host History window, while the close button dismisses the card. Capture paths are never opened from the toast.
- **Settings config persistence** — Settings reads a side-effect-free snapshot, applies staged keys with per-key results, and writes through a flushed same-directory temp file replacement. Toast idle duration accepts 10/20/30/60 seconds; send-code journal retention accepts 1–48 hours and preserves the selected duration when Off.

## v0.6.3 - Localized UI and a Revit-only installer

### Added

- **Plugin UI in 15 languages** — ribbon, tooltips, toasts, History window, dialogs and Bake Inbox follow Revit's UI language, or the **Language** combo in the ribbon slide-out (saved as `uiLanguage`; the `BIMWRIGHT_UI_LANGUAGE` environment variable takes precedence). Translations are machine-generated; fix one without a rebuild in `%LOCALAPPDATA%\RvtMcp\locales\strings.<locale>.json` — overrides are validated (known keys, matching placeholders, locked `security.*` warnings) and hot-reloaded. Tool names, schemas and wire payloads stay English. See [docs/localization.md](docs/localization.md).
- **History window search and past sessions** — search matches summary, params and error text, with a Read/Write kind filter. **Load past sessions** reads archived `mcp-calls-*.jsonl` logs as read-only rows; **Open logs** opens `%LOCALAPPDATA%\RvtMcp`.
- **`send_code` re-run from the journal** — redacted `send_code` entries can re-run by matching `code_hash` in `send-code-journal.jsonl`, including rotated archives, after an extra confirmation that the body is bake-redacted. Entries with no recoverable body show why Re-run is disabled.
- **"Agent connected" toast** when an MCP client attaches to the plugin transport.

### Changed

- **Toasts** — BIMwright wordmark in the footer with a brand sweep reveal (matching ipt-mcp). Toasts are held while the Revit window is minimized, hidden or blocked by a modal dialog, then shown once it is usable again. Toasts no longer take keyboard focus from Revit.
- **Ribbon and History window** — the ribbon keeps Toggle + History only (Status button removed); the toast toggle shows a dot (yellow on, gray off). The History window gets BIMwright styling, centered columns, a collapsible detail pane, a glyph status column, and **New Session** (was Clear Session) behind a confirmation.
- **Session log bounds** — in-memory live-session history caps at 1,000 entries (evicted rows reload from the log as history); params over 64 KB are truncated and cannot re-run; file-log field caps raised to params 8 KB, result 10 KB, error 4 KB.
- **Brand strings** centralized in `src/shared/Views/BrandAssets.cs` for forks that rebrand.
- **Installer no longer edits MCP client configs** — it installs the Revit add-ins and the server only; connect clients with their own tools (the agent procedure is in `AGENTS.md`). `-Client`/`-WireClient` are deprecated and only warn. The uninstaller no longer edits client configs either.
- **Fixed server path** — the server installs to `%LOCALAPPDATA%\RvtMcp\rvt\server\current\rvt-mcp.exe` for every version, so clients keep working across updates and only need a restart. A previous copy that a running client still uses is kept and removed at the next install; older versioned folders are listed and removed with `-PruneOldServers`.
- **Installer verifies the Revit side** — Revit years count only when `Revit.exe` exists (leftover registry keys are ignored); plugin ZIP manifests must carry RvtMcp's AddInId; per-user add-ins with the same AddInId (Bimwright-era copies) are removed inside the rollback-able transaction; a machine-wide copy under `%ProgramData%` blocks the install; installed add-ins are compared byte for byte with the package; the server is unblocked (Mark-of-the-Web) and started once with `--help`. Any failure restores the previous add-ins and server.
- **Add-in uninstall covers every year** — `install.ps1 -Uninstall` removes RvtMcp add-ins (and same-AddInId legacy copies) for 2022–2027 regardless of which Revit is still installed.
- **Setup packages must match a commit** — `package-client-setup.ps1` refuses an uncommitted working tree unless `-AllowDirty` (recorded as `dirty` in the manifest), and ships `AGENTS.md`.
- **Full uninstall keeps personal data by default** — step 4 removes only server copies, discovery files and the spill cache; settings, locales, ToolBaker data, firm profiles, shared parameters, logs and captures stay. `-Purge` deletes the whole folder, `-Purge -KeepLogs` keeps logs.
- **Plugin DLLs are version-stamped** — all six shells now carry `FileVersion`/`ProductVersion` from `<Version>` (previously `0.0.0.0` because `GenerateAssemblyInfo` was off with no manual AssemblyInfo). The manual `SupportedOSPlatform` attribute stays via `GenerateTargetPlatformAttribute=false`.

### Fixed

- **History privacy** — redact the grid Summary as well as detail fields, including raw output returned by successful `send_code` re-runs.
- **History count and cap** — past-session rows stay pinned and no longer count toward the 1,000 live-row cap or ribbon badge; New Session leaves the live count at zero.
- **Cached `send_code` re-runs** — recover missing code snippets from executed parameters when body caching is enabled, preserving the numbered code view and subsequent re-runs without retaining bodies when caching is off.
- **Concurrent log writes** — coordinate call-log and send-code-journal append, rotation, maintenance and file reads with per-path cross-process mutexes. Lock timeouts never fall back to unlocked writes; logging remains best-effort on timeout or I/O failure.
- `send_code` compilation no longer fails when a stale add-in DLL is still loaded in the AppDomain but its file is gone.
- The startup "Agent connected" toast shows immediately instead of waiting for a project to open.
- **Uninstall no longer half-deletes a running server** — a server copy still used by an MCP client is kept whole and reported; close the client and run again.
- **Reported `serverInfo.version` no longer drifts** — it was hardcoded (`"0.6.2"`) and now derives from `AssemblyInformationalVersion` (semver without the git-hash suffix).

### Docs

- README credits community bug reports and proposals; the License section adds a note on forks and optional credit.
- **[docs/mcp-client-wiring.md](docs/mcp-client-wiring.md)** — verified per-client procedures for wiring `rvt-mcp` (CLI commands, config paths, entry shapes, scope-shadow gotchas) across the MCP clients an agent may meet; `AGENTS.md` Step 3 and the READMEs link to it, and the three `docs/mcp-config-*.md` files were refreshed to the `current` path and point back to it.

## v0.6.2 - Safer upgrades and placement/MEP fixes

### Added

- **Send-code source forms and failure handling (#14)** — accept C# bodies with helper type declarations and provide the opt-in `SafeFailuresPreprocessor` for inspectable warnings and error rollback. Warning suppression does not establish design compliance. See [source forms and failure handling](docs/send-code.md).

### Fixed

- **Installer upgrades** — preserve existing MCP options and unrelated Codex TOML sections, keep `-WhatIf` free of file writes, reject running Revit and invalid packages before replacement, and restore plugin/server/config changes after caught installation errors. Add Windows PowerShell 5.1 and PowerShell 7 upgrade/rollback regression coverage.
- **MEP membership (#11)** — use piping/HVAC network collections, deduplicate inventory with terminals/base equipment, and count open physical connectors only in the system's domain. Failed reads no longer imply an empty system.
- **Pipe system inheritance (#12)** — omitted system type inherits from a unique open piping connector at the start and connects during creation. Ambiguity and diameter conflicts are rejected; fallback reports the actual type. MEP connections reject different assigned piping/HVAC system types before mutation.
- **Connections through fittings (#12 follow-up)** — recognize connections through one shared pipe/duct fitting before choosing unused ports and after `ConnectTo`. Repeated calls no longer connect the opposite free ends of already joined curves. Correct public pipe selector names in the behavior guide.
- **Hosted family placement (#13)** — optional `host_id`, placement-type validation, explicit hosts for hosted families, and actual host/position checks before commit. Unsupported face/work-plane placement and mismatches fail without leaving an instance behind.
- **Toast crash** — initialize window coordinates before showing/reflowing; avoid WPF animations from `NaN` and clear stale position animation clocks. Includes a real WPF regression executable.
- **Build/deployment dependencies** — pin patched native SQLite, include net48 runtime dependencies, and fail deployment/package staging if native SQLite is missing. Restore modern plugin Windows platform annotations without suppressing analyzer rules.

### Changed

- Placement and MEP contracts are documented in [the behavior guide](docs/placement-and-mep-contracts.md). These schema changes require updating the server and plugin together and restarting Revit and the MCP connection.
- **Stairs (#14)** — documented conversation/send-code workflow, tested examples and execution safeguards; a dedicated stair tool remains deferred. See [coverage and limits](docs/stairs-workflow.md).
- **Completion toast now defaults ON** — fresh installs show result-only toasts out of the box. Disable via ribbon **Toast** (persisted), `enableToast: false`, or `BIMWRIGHT_ENABLE_TOAST=0`. Existing explicit `enableToast` config values are untouched.
- Optional developer path: NuGet global tool **`RvtMcp.Server` 0.6.1** (MCP server only; Revit plugins still come from the GitHub Release ZIP). Legacy **`Bimwright.Rvt.Server` 0.1–0.3** is obsolete.

## v0.6.1 - Project and link coordinate inspection

First GitHub Release after v0.5.0 was unpublished. The client setup ZIP is `RvtMcp.Setup-v0.6.1-win-x64.zip` (includes the v0.6.0 guardrail surface plus the tools below).

### Added

- **Project/link coordinate inspection** — `revit_get_project_coordinate_system` reports Internal Origin, Project Base Point, Survey Point, named Project Locations, True North, and site coordinates. `revit_get_link_coordinate_system` adds Revit/CAD link transforms, maps linked origins into host coordinates, and exposes linked Project Location ids for publish workflows.

### Changed

- **Coordinate workflow descriptions** now document Revit/CAD acquire support, publish preflight, confirmation requirements, and CAD publish limitations.
- Tool counts: default **40**, `--toolsets all` **229**, adaptive bake **232**.

## v0.6.0 - Agent guardrails, oversized-response spill, and toast/privacy

Not published on its own — this surface first shipped in v0.6.1.

### Added

- **MCP activity toast (plugin)** — result-only completion notifications (no in-progress toast). Default **off** (`enableToast` / `BIMWRIGHT_ENABLE_TOAST` / ribbon **Toast**). Capture success can show a path-allowlisted thumbnail; `send_code` results redacted on the toast surface.
- **Opt-in `send_code` body journal (TTL)** — `persistSendCodeBodies` + `persistSendCodeBodiesUntil` (CLI/env/JSON); plugin writes redacted journal under `%LOCALAPPDATA%\RvtMcp\`.
- **Capture path UX** — clearer allowlist errors; optional default output under captures.
- **Status dialog privacy/bake flags** — ribbon **Status** lists toast, ToolBaker, adaptive bake, body cache, and persist journal (read-only snapshot for operators). Unit-tested via `StatusPrivacySection`.
- **Local oversized-response spill** — eight approved bulk tools accept `output=inline|file` and write SQLite, NDJSON, JSON, or text artifacts under `%LOCALAPPDATA%\RvtMcp\spill\`. Responses include an absolute local path, format/schema, true byte count, and a bounded preview. `revit_send_code_to_revit` and oversized `revit_run_baked_tool` output auto-spill. Files older than 24 hours are removed and the directory is capped at 50 artifacts.

### Changed

- **Default toolsets narrowed** to `query`, `create`, `view`, `meta` (**40** tools) so weak models are not flooded with the full catalog. Clash, export, MEP, structural, ToolBaker, and the rest stay available via `--toolsets all` or an explicit CSV.
- **`batch_execute` capped at 20** sub-commands (plugin + server fail-fast).
- **Oversized responses use a layered policy** — agent-visible warning from 64 KiB, strong warning above 256 KiB, and a ~700 KiB compact-response enforcement budget that leaves headroom below the 1 MiB delivered ceiling. Scoped reads receive command-specific narrowing guidance; completed mutations retain truthful `success=true` summaries instead of inviting unsafe retries.
- **Bulk response scope hardened** — all 97 surveyed scoped-risk tools have command-specific recovery hints; 23 previously unscoped tools now expose bounded filters/paging or compact response controls.
- **60s request timeout completes the plugin TCS** so a late handler result cannot ride the next UI tick. Clash/export tool descriptions tell agents not to retry after timeout.

### Operational notes

- SQLite spill is opt-in bulk work and is written synchronously on Revit's UI thread through the `ExternalEvent` execution boundary. Very large exports (tens of thousands of rows) may briefly make the Revit UI feel unresponsive while the artifact is written.
- Spill paths are local same-machine paths. Remote MCP clients can inspect the bounded preview and schema but cannot open the artifact directly.

### Docs

- English README rewritten (human tone); vi / zh-CN / ja ports aligned. Tool counts: default **40** (`query,create,view,meta`), `--toolsets all` **227**, adaptive **230**.
- Clarified product scope: send_code for out-of-scope work; no Python host / Viewer / Family Editor suite this cycle.
- `docs/roadmap.md` non-goals updated.

### Product posture

- Out-of-typed-tool workflows → **`revit_send_code_to_revit` (C# only)**.
- **Will not do this cycle:** Python send_code host, Revit Viewer support, Family Editor authoring tool suite (#7).

### Acknowledgements

- Thanks to [@Thestreetarckitect](https://github.com/Thestreetarckitect) for the detailed Family Authoring Tool Suite proposal in #7, which helped clarify the roadmap and this release's scope.

## v0.5.0 - Tool Search discoverability + multi-Revit routing (BREAKING)

Two pains addressed in one release:

1. **Agents couldn't find any rvt-mcp tools** even though 224 were exposed (Tool Search returned nothing because `instructions` field was empty + tool names carried no "revit" semantic signal). Failure mode warned about in `docs/mcp-config-claude-clients.md` §5.3.
2. **Multi-Revit routing was opaque to agents** — when two Revits were open and the user said "check Revit 2024 ...", agents kept guessing `R24`/`R25` style codes, hitting the wrong instance, or routing to whichever auto-detect happened to pick first. No way to discover what was running, no clear contract on the year string format.

### Breaking changes

- **All 226 MCP tool names now prefixed with `revit_`.** `create_grid` → `revit_create_grid`, `analyze_structural_connections` → `revit_analyze_structural_connections`, etc. Wire-protocol command names (server↔plugin) are unchanged — only the MCP-facing names that clients/agents see. Any user scripts, slash commands, or saved permission rules that reference old tool names by literal string need updating.
- **Discovery file format and naming changed.** Old: `portR22.txt`, `pipeR25.txt`, etc. (one file per version, multi-line text). New: `revit-2022.json` through `revit-2027.json`, one file per Revit, self-describing JSON. Plugin and server BOTH need to be on v0.5+ for connection to work — mixing v0.4 plugin with v0.5 server (or vice versa) breaks discovery. Old `port*.txt`/`pipe*.txt` files are auto-deleted by the v0.5 server on first startup.
- **Version strings unified to 4-digit calendar years.** `--target 2024` (not `--target R24`), `revit_switch_target("2024")` (not `"R24"`). Legacy R-codes are rejected with an educational error pointing at `revit_list_available_targets`. Affects `--target` CLI flag, `BIMWRIGHT_TARGET` env var, JSON config `target` field, `revit_switch_target` tool param, plus the version string used by ToolBaker compatibility tracking.
- `--toolsets structural` is now enabled by default. The 12 structural tools (`revit_create_structural_column`, `revit_create_rebar_set`, etc.) appear in the default surface. Use `--toolsets query,view` etc. to opt out of write-capable structural tools, or `--read-only` to strip all write toolsets including structural.

### Added

- **Server `instructions` field** populated at server startup (`Program.cs::ConfigureMcpServerOptions`). ~2 KB of keyword-dense text leading with "rvt-mcp — MCP gateway for Autodesk Revit 2022-2027" followed by every domain term (wall, door, MEP, duct, pipe, structural, IFC, DWG, NWC, etc.) and a per-toolset tool-name index. Includes explicit multi-Revit hint: "if >1 Revit may be open, call revit_list_available_targets THEN revit_switch_target". This is the primary signal Tool Search ranks on.
- **Server `ServerInfo`** metadata (name, title, version, description, websiteUrl) populated for richer client UIs.
- **2 new meta tools** for multi-Revit routing:
  - `revit_list_available_targets` — reads `%LOCALAPPDATA%\RvtMcp\revit-*.json`, returns every running Revit with `{year, transport, port|pipe_name, pid, discovery_file, is_currently_connected}`. Agent calls this FIRST when uncertain which version is available.
  - `revit_get_current_target` — returns `{pinned_target, currently_connected_year, discovery_dir}` so an agent can verify which Revit will receive the next command.
- **Hard validation on `revit_switch_target`**: passing an R-code like `"R24"` returns a structured error with `recommended_next_tool: "revit_list_available_targets"` and a translation table (R22=2022 .. R27=2027). Forces the agent to read the available-targets output rather than guess.
- **`Add-KiloEntry`** in `scripts/install.ps1` — Kilo Code CLI users are now wired automatically via `~/.config/kilo/kilo.json` (writes `type=local`, array-form `command`, `timeout=30000`, optional `environment` block).
- **`environment` block support** in `Add-OpencodeEntry` and `Add-KiloEntry` — when a target object includes an `Env` hashtable, it is emitted under the `environment` key (closes the gap documented in `docs/mcp-config-opencode-kilo.md` §5).
- **`env` block support** in `Add-ClaudeEntry` and `Add-CodexEntry` (Claude: `env` field per `mcp-config-claude-clients.md` §4.3; Codex: `[mcp_servers.X.env]` sub-table per `mcp-config-codex.md` §2). Parity with the JSON variants.
- **`-Client kilo`** option in `install.ps1`. Auto mode now wires kilo alongside opencode, codex, and claude.

### Fixed

- `docs/mcp-config-opencode-kilo.md` §3.6 tool count corrected from 248 to 224 (the 248 figure conflated method-level `[McpServerTool]` attributes with 24 class-level `[McpServerToolType]` attributes).
- **Claude Code `tools fetch failed`** — 4 tools (`revit_create_dimensions`, `revit_create_filled_region`, `revit_create_room_separator`, `revit_set_titleblock_parameters`) had required `object` parameters which the C# MCP SDK emitted as JSON Schema boolean shorthand `true`. Anthropic's Zod validator rejects boolean shorthand even though it is spec-compliant. Param types changed to `object[]` (arrays) and `IDictionary<string, object>` (parameters map) so the SDK emits proper `{"type":"array","items":{}}` / `{"type":"object"}` schemas. No agent-visible API change.
- **`revit_send_code_to_revit` returned `<result_1>` placeholder instead of real data.** The plugin previously routed live wire responses through `BakeRedactor.RedactForBake(..., redactResultFields:true)` so any string in the `result` field was replaced by a generated token. The five persistence paths (`McpLogger`, `McpSessionLog`, `JournalEntry`, `AcceptBakeSuggestionHandler`, `UsageEventLogger`) each call `BakeRedactor` independently at write time, so logs/journals/bake suggestions stay redacted regardless of what the wire returns. The wire now returns the raw `output` from the user's `Run(UIApplication)` body — anonymous objects serialize as structured JSON, strings as strings, numbers as numbers, collections as arrays. Agents no longer need to dump to disk + re-read.

### Migration

User data (`%LOCALAPPDATA%\RvtMcp\baked\`, `journal\`, `firm-profiles\`) is unaffected.

**Required steps when upgrading from v0.4:**

1. Close all running Revit instances.
2. Install v0.5 plugin into `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` (server writes `revit-YYYY.json`; v0.4 server reading `portR22.txt` won't work).
3. Install v0.5 server via the bundled installer ZIP from the GitHub Release (`pwsh install.ps1`). A NuGet `dotnet tool` package will follow in a later patch.
4. Restart Revit. Old `portR*.txt`/`pipeR*.txt` discovery files are deleted automatically by the v0.5 server on first startup.

**Config-side changes for hand-edited setups:**

| Old | New |
|---|---|
| `send_code_to_revit` | `revit_send_code_to_revit` |
| `batch_execute` | `revit_batch_execute` |
| `rvt-mcp_create_grid` (OpenCode/Kilo permission glob) | `rvt-mcp_revit_create_grid` |
| `mcp_servers.rvt-mcp.tools.batch_execute` (Codex per-tool) | `mcp_servers.rvt-mcp.tools.revit_batch_execute` |
| `--target R24` | `--target 2024` |
| `BIMWRIGHT_TARGET=R24` | `BIMWRIGHT_TARGET=2024` |
| `revit_switch_target("R24")` | `revit_switch_target("2024")` |
| Discovery file `portR24.txt` / `pipeR26.txt` | `revit-2024.json` / `revit-2026.json` |

## v0.4.0 - Full Revit tool surface

Tool surface grew from 32 → **249 tools** (default) / **254 tools** (adaptive bake), plus a new opt-in **structural** toolset (12 tools) gated behind `--toolsets structural`. Default tool count badge: 249. Adaptive bake badge: 254.

### Added — Wave 14 (structural, opt-in)

- Added new opt-in `structural` toolset (12 write-capable tools, NOT in `DefaultOn` — enable with `--toolsets structural`):
  - **Structural elements (5)**: `create_structural_column`, `create_structural_beam`, `create_structural_wall`, `create_foundation_isolated`, `create_foundation_wall`.
  - **Rebar (3)**: `list_rebar`, `create_rebar_set` (Single / FixedNumber / MaximumSpacing layouts), `create_rebar_stirrup` (shape-driven).
  - **Loads & analysis (3)**: `get_structural_loads`, `set_structural_load` (update only; create deferred), `analyze_structural_connections`.
  - **Tagging (1)**: `tag_structural_framing`.

### Added — Wave 15 (final fill: meta / lint / view)

- Added 8 high-value handlers extending existing toolsets:
  - **Meta (2)**: `set_project_info` (typed fields: name/number/client_name/address/status/issue_date), `purge_unused` (families-only MVP, `dry_run` defaults to true, skips in-place families and instance-referenced symbols).
  - **Lint (1)**: `get_model_warnings_summary` (groups `doc.GetWarnings()` by description, includes example failing element ids).
  - **View (5)**: `capture_view_image` (sandboxed to `%TEMP%` or `%LOCALAPPDATA%\RvtMcp\captures\`), `set_view_crop` (explicit bounds or fit-to-elements with padding), `set_view_scale`, `activate_view`, `show_element_in_view`.

### Changed — Wave 15 (read-only guard)

- Added `ServerState.BlockIfReadOnly` per-tool guard helper. View-write tools (`capture_view_image`, `set_view_crop`, `set_view_scale`, `activate_view`, `show_element_in_view`) plus `set_project_info`, `purge_unused` (when `!dry_run`), and `set_structural_load` (when `action='update'`) now refuse with a structured `read_only_mode` payload under `--read-only`. The `view` toolset stays in `DefaultOn` (read-only operations like `analyze_sheet_layout` remain available in read-only mode).

### Added — Wave 16 (rebar follow-up)

- Implemented the 2 rebar handlers deferred from Wave 14 with `#if REVIT2027_OR_GREATER` guard for the `Rebar.CreateFromCurves` signature change in R27 (legacy `RebarHookType`/`RebarHookOrientation` overload removed; new `BarTerminationsData` overload required).

### Added — Earlier in this release (Waves 1-13)

- Added 34 new MCP handlers across 3 new toolsets (increasing the total non-adaptive surface to 143 tools across 17 toolsets):
  - **Sheets (12 tools)**: `create_sheet`, `duplicate_sheet`, `create_placeholder_sheet`, `list_sheets`, `set_titleblock_parameters`, `get_titleblock_parameters`, `list_titleblocks`, `place_schedule_on_sheet`, `create_revision`, `assign_revision_to_sheet`, `list_revisions`, `renumber_sheets`.
  - **Materials (10 tools)**: `list_materials`, `get_material_properties`, `create_material`, `duplicate_material`, `set_material_appearance`, `set_material_identity`, `set_material_structural_asset`, `set_material_thermal_asset`, `assign_material_to_element`, `get_material_takeoff`.
  - **Geometry Analysis (12 tools)**: `get_element_bounding_box`, `get_element_geometry`, `measure_distance_between_elements`, `clash_detection`, `raycast_from_point`, `find_elements_in_volume`, `compute_element_volume`, `compute_element_area`, `project_point_onto_face`, `find_overlapping_elements`, `get_element_centroid`, `analyze_geometry_complexity`.
- Added 32 MCP handlers across annotation, rooms, and links (increasing the total non-adaptive surface to 175 tools across 19 toolsets):
  - **Annotation / Detail (12 tools)**: `tag_elements`, `tag_all_by_category`, `create_text_note`, `create_dimensions`, `create_filled_region`, `create_detail_line`, `create_callout_view`, `list_keynotes`, `apply_keynote_to_element`, `find_untagged_elements`, `find_undimensioned_elements`, `wipe_empty_tags`.
  - **Rooms / Areas / Spaces (10 tools)**: `list_rooms`, `get_room_boundaries`, `get_room_openings`, `create_room_separator`, `create_area`, `create_space`, `list_areas`, `compute_room_finishes`, `auto_create_rooms_from_walls`, `tag_all_areas`.
  - **Links / CAD / Coordinates (10 tools)**: `list_linked_models`, `list_linked_cad`, `import_cad_to_view`, `link_revit_model`, `unload_link`, `reload_link`, `get_link_elements`, `acquire_coordinates_from_link`, `publish_coordinates_to_link`, `set_project_base_point`.

### Changed

- Hardened new annotation, rooms, and links handlers around dry-run semantics, ElementId compatibility checks, destructive link scope validation, and transaction commit-status reporting.
- Made `send_code_to_revit` part of the default ToolBaker surface, without adaptive-bake gating or per-call Revit confirmation.
- Changed installer client wiring to a single auto-detect `rvt-mcp` MCP entry while still deploying plugins for every detected Revit year.
- Added 15 non-schedule Revit data tools: 10 read tools for elements, parameters, groups, assemblies, and worksets, plus 5 write tools for parameters, type changes, worksets, and group creation.
- Added 10 Revit schedule tools in a new default-on `schedule` toolset: `list_schedules`, `get_schedule_definition`, `get_schedule_data`, `get_schedule_formulas`, `get_schedulable_fields`, `find_schedule_elements`, `create_schedule`, `add_schedule_field`, `update_schedule_field`, `apply_schedule_filter_sort`.

## v0.3.1 - Client setup installer

### Added

- **Client setup ZIP** — self-contained Windows setup flow for Revit client machines: installer wiring, uninstall support, no-deploy plugin builds, and CI artifact upload.

### Changed

- README narrative rewrite (personal-automation positioning); translated READMEs and install guides synced (#3, #4, #5).
- Post-v0.3.0 cleanup: docs sync, dependency bumps, install fixes.

## v0.3.0 - ToolBaker redesign

### Breaking

- Removed `bake_tool`. It is no longer available as an MCP tool. Create new baked tools through the adaptive-bake suggestion flow and `accept_bake_suggestion` instead.

### Added

- Added adaptive-bake suggestion lifecycle tools: `list_bake_suggestions`, `accept_bake_suggestion`, and `dismiss_bake_suggestion`.
- Added accepted-tool indirection through `list_baked_tools` and `run_baked_tool`, including parameter validation and archived-tool guidance.
- Added Revit ribbon/runtime support for accepted baked tools, backed by a plugin runtime cache.
- Added local SQLite-backed bake storage (`bake.db`) plus local audit/usage records under `%LOCALAPPDATA%\RvtMcp\`.
- Added compiler policy, privacy, registry, runtime cache, usage clustering, and ToolBaker handler tests.

### Changed

- Adaptive bake is opt-in and default off via `BIMWRIGHT_ENABLE_ADAPTIVE_BAKE=1` or `"enableAdaptiveBake": true`.
- Usage data for adaptive bake is stored locally under `%LOCALAPPDATA%\RvtMcp\`.
- Accepted baked tools are discovered with `list_baked_tools` and executed with `run_baked_tool name=<tool_name>`.
- The Revit plugin reads `bake.db` and owns runtime/ribbon loading only; the server is the sole SQLite writer.
- `send_code_to_revit` is gated behind adaptive bake and continues to require per-call Revit confirmation.
- Baked tools no longer appear as promoted native MCP tools in v0.3.x; agents use the stable accepted-tool index instead.
- README and docs now reflect the current 32-tool default surface, 35-tool adaptive surface, supported MCP clients, and R22/R26/R27 accepted-tool smoke evidence.

### Fixed

- Fixed baked-tool dispatch bypasses by preventing `run_baked_tool` execution through `batch_execute` and isolating baked commands from core command lookup.
- Fixed stale cross-version compatibility metadata by refreshing baked-tool registry reads from `bake.db` before list/meta reads.
- Fixed Revit 2026/2027 accepted-tool startup failures by copying native `e_sqlite3.dll` beside `RvtMcp.Plugin.dll` during deploy and release staging.
- Fixed suggestion refresh hardening: bounded usage replay, capped/deduped suggestions, and guarded adaptive usage capture.

### Security

- Hardened durable logs, journals, prompts, markdown, and live responses so send-code outputs and sensitive literals are redacted or hashed before persistence.
- Added redaction boundary fixes for paths, filenames, escaped outputs, and legacy orphan call archives.
- Added compiler denylist coverage and plugin allow-list narrowing for baked C# execution.

## v0.2.1 - Lint toolset + switch_target

### Added

- **`lint` toolset (default on)** — `analyze_view_naming_patterns`, `suggest_view_name_corrections`, `detect_firm_profile`: view-name pattern extraction, outlier detection with edit-distance suggestions, and firm-profile detection with an empty-folder fallback. Firm-profile schema documented under `docs/firm-profiles/`.
- **`switch_target`** — MCP tool to choose which running Revit version receives commands (#2).
- MCP `ToolAnnotations` on tools, with tightened tool descriptions.

### Changed

- Ribbon: BIMwright moved into the Add-Ins tab, with a Status button.
- README: full tools table, Project Structure section (EN / vi / zh-CN), corrected Revit 2025–2026 transport.

## v0.2.0 - Tool-logic review backlog closed

### Fixed

- **ToolBaker** — atomic registry writes, thread-safe access and quarantine of corrupt registry files; `run_baked_tool` counts only successful calls; assembly-version conflicts during compilation are logged.
- **`delete_element`** returns `deletedIds` / `failedIds` / `errors` so callers can act on partial failures.
- **Large models** — `analyze_model_statistics` and `get_model_overview` stop at 100,000 elements and report truncation, so Revit no longer freezes on big federated/IFC models.
- **`get_selected_elements`** returns `staleIds` for elements deleted between selection and retrieval.
- The bake confirmation dialog shows the full code behind **Show details** instead of truncating at 300 characters.

### Docs

- README rewritten in an engineer voice; added the zh-CN mirror; new rvt-mcp product logo and bimwright footer.

## v0.1.2 - Critical tool-logic fixes

### Fixed

- **`operate_element`** — schema enum now matches the handler (`select`, `hide`, `unhide`, `isolate`, `setcolor`); it previously advertised a non-overlapping set.
- **`create_room`** — uses `NewRoom(level, point)` on Revit 2022–2027; the 2023+ path threw a NullReferenceException.
- **`create_surface_based_element`** — returns a clean "No floor/ceiling type loaded" error on empty projects instead of crashing.

Plugin DLLs changed; no breaking changes.

## v0.1.1 - Client wiring, uninstall-all, agent-led install

### Added

- `install.ps1 -WireClient opencode|codex` for scripted MCP client config; `uninstall-all.ps1` for one-pass removal (tool, plugin, client configs, discovery files, cache).
- `AGENTS.md` — agent-readable install guide for 9 MCP clients, with preview / approval / rollback rules.
- Golden snapshot tests for the MCP tool list; response-size observability (`ResponseSizeGuard`).
- Listed on the MCP Registry as `io.github.bimwright/rvt-mcp`; `smithery.yaml` for Smithery.

### Changed

- Repo renamed `bimwright/bimwright` → `bimwright/rvt-mcp`; namespaces `Bimwright.*` → `Bimwright.Rvt.*`.
- README rewrite, Vietnamese mirror `README.vi.md`, plus `SECURITY.md` and `CODE_OF_CONDUCT.md`.

## v0.1.0 - Public launch

- MCP gateway for Autodesk Revit 2022–2027: 28 tools across 10 toolsets.
- Progressive disclosure with `--toolsets` and `--read-only`.
- `batch_execute` with Revit `TransactionGroup` semantics.
- ToolBaker self-evolution (Debug builds only).
- Security: loopback by default, token auth, strict schema validation, path-leak masking.
- Packaging: .NET global tool, per-year plugin ZIPs and `install.ps1`; CI matrix for Revit 2022–2027.
