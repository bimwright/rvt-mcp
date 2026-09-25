# Roadmap

Rough direction, not a commitment. Dates are intent; scope is firmer.

## v0.3.0 — current (ToolBaker redesign release)

- 47 MCP tools across 11 toolsets; 50 tools when adaptive bake is enabled.
- Progressive disclosure (`--toolsets`, `--read-only`).
- `batch_execute` with Revit `TransactionGroup` semantics.
- ToolBaker accepted-tool indirection via `list_baked_tools` / `run_baked_tool`; `send_code_to_revit` is default-on and adaptive suggestions remain default off.
- Security: loopback default, token auth, strict schema validation, path-leak mask.
- Packaging: client setup ZIP with self-contained server + per-year plugin ZIPs; NuGet `dotnet tool` remains for developer/legacy install.
- CI: matrix R22–R27 + server pack + xUnit.

Compile gate is 6/6; unit coverage is pure .NET; runtime smoke still needs per-Revit verification before a broad release.

## v0.2 — hardening + surface expansion

- **MCP Resources** — expose model-level context (active doc, current view, selected elements, recent commands) as MCP `resources` alongside tools, so clients with resource support can browse state without spending a tool call.
- **ToolBaker G1–G4 gaps** — path escaping in generated handlers, per-tool capability sandbox, signed-bake verification, easier re-bake on Revit version bump.
- **Test project structure** — revisit "option 2" (per-file `Compile Include`). If the test suite is growing, promote `src/shared/` to a real class library so tests reference one project instead of cherry-picking files.
- **AspNetCore slim-down** — server is currently `Microsoft.NET.Sdk.Web` so the `.nupkg` drags ~40 AspNetCore DLLs even for stdio-only users. Either split `RvtMcp.Server` (stdio) from `RvtMcp.Server.Http` (SSE), or conditionally pull in AspNetCore only for the HTTP path.
- **Plugin ZIP size** — strip non-win-x64 entries from `runtimes/` in `scripts/stage-plugin-zip.ps1`. R25+ zips drop from ~16 MB → ~5 MB.
- **Testing & drift detection (aspect #7)** — _delivered 2026-04-18_ — golden snapshot of the MCP tool surface diffed on every test run; manual Haiku benchmark procedure (`benchmarks/`) with trigger rules and 15% regression threshold; S4 response-size observability hook (passive stderr warning, no enforcement).
- **View-naming lint (L-05 + L-13)** — _delivered 2026-04-23 (v0.2.1)_ — 3 new read-only tools in a new `lint` toolset (default on): `analyze_view_naming_patterns`, `suggest_view_name_corrections`, `detect_firm_profile`. Firm-profile library scaffolding in place (`docs/firm-profiles/README.md`); no profiles shipped yet.

## v0.4 / next — hardening + ecosystem

- **Gap draft status** — `dismiss_bake_suggestion` can turn repeated send-code patterns into a local GitHub issue draft URL for user review. Bimwright does not submit the issue or send telemetry.
- **Async job polling (A8)** — long-running Revit operations (full-model recompute, export-to-IFC, large family load) currently block the 30 s response timeout. Add a `jobs/status/<id>` pattern so the model can fire and check later.
- **Aggregator listings** — submit to Smithery, mcp.so, PulseMCP, MCP Market, Cline's registry, MseeP. Each has its own metadata format; roll changes through `server.json` first where possible.
- **Prompt library** — _delivered 2026-09-25_ — 4 MCP prompts (`revit_getting_started`, `revit_model_audit`, `revit_pre_issue_check`, `revit_stairs`) as embedded markdown bodies with render-time toolset gating. Generic prompts only; no project-specific DB coupling.
- **R27 GA promotion** — when .NET 10 ships GA and R27 is widely installed, drop the "experimental" caveat.

## v1.0 — governance + stability

- **Governance model** — maintainer policy, contribution tiers, review SLA. Open to co-maintainers if the project crosses a contributor threshold.
- **Domain registration** — `bimwright.dev` or similar. Canonical docs site instead of GitHub Pages.
- **API stability commitment** — tool schemas versioned, breaking changes require a deprecation cycle.
- **SECURITY.md with disclosure process** — named contacts, response-time commitment, CVE workflow.
- **Enterprise flags** — signed plugin DLLs, centralized config deployment, Windows installer (`.msi`) option.

## Deferred / explicit non-goals

- **No macOS / Linux support.** Revit is Windows-only; supporting the server alone without the plugin is noise.
- **No GUI for the server.** CLI + config file is the whole story. The plugin's ribbon panel is Revit-side only.
- **No non-MCP transports** (stdio + HTTP+SSE is it for this project). gRPC, Thrift, etc. won't be added.
- **No Python (or IronPython) `send_code` host (2026-07-15).** Escape hatch stays **C#** via `revit_send_code_to_revit`. Out-of-surface workflows use that tool, not a second language runtime.
- **No Autodesk Revit Viewer host (2026-07-15).** Full Revit desktop only; no Viewer discovery or tool profile.
- **No Family Editor authoring toolset this cycle (2026-07-15).** Project-level `families` management remains. Definition/session authoring (`revit_family_*` / issue #7) deferred to limit agent context size; use `send_code` instead. Design retained under `docs/superpowers/specs/2026-06-18-family-authoring-phased-plan-design.md`.

Close-out note: `docs/analysis/2026-07-15-product-decisions-closeout.md`.

## Security notes

Current v0.3.0 hardening covers launch-day concerns. Deferred:

- **S4 pagination** — tool responses can be large (100-item DTO arrays). No pagination contract yet; client is on its own for chunking.
- **Signed ToolBaker bakes** — baked tools now persist in server-owned SQLite metadata under `%LOCALAPPDATA%\RvtMcp\bake.db`, but accepted bake artifacts are not signed. A malicious same-user process with file-write access to local Bimwright storage could still tamper with local artifacts. Signed-bake verification remains planned hardening work; acceptable for single-user dev, v1.0 territory for shared environments.
- **LAN bind warning** — `BIMWRIGHT_ALLOW_LAN_BIND=1` flips to `0.0.0.0` with only a stderr warning. Consider requiring a second env var or confirming on first run.

If you're running Bimwright in an environment where any of these matter, open an issue — it helps prioritize.
