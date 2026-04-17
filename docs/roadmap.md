# Roadmap

Rough direction, not a commitment. Dates are intent; scope is firmer.

## v0.1.0 — current (public launch)

- 28 MCP tools across 10 toolsets.
- Progressive disclosure (`--toolsets`, `--read-only`).
- `batch_execute` with Revit `TransactionGroup` semantics.
- ToolBaker self-evolution (Debug only).
- Security: loopback default, token auth, strict schema validation, path-leak mask.
- Packaging: `dotnet tool` + per-year plugin ZIPs + `install.ps1`.
- CI: matrix R22–R27 + server pack + xUnit.

Shipped on compile + one smoke test per Revit year.

## v0.2 — hardening + surface expansion

- **MCP Resources** — expose model-level context (active doc, current view, selected elements, recent commands) as MCP `resources` alongside tools, so clients with resource support can browse state without spending a tool call.
- **ToolBaker G1–G4 gaps** — path escaping in generated handlers, per-tool capability sandbox, signed-bake verification, easier re-bake on Revit version bump.
- **Test project structure** — revisit "option 2" (per-file `Compile Include`). If the test suite is growing, promote `src/shared/` to a real class library so tests reference one project instead of cherry-picking files.
- **AspNetCore slim-down** — server is currently `Microsoft.NET.Sdk.Web` so the `.nupkg` drags ~40 AspNetCore DLLs even for stdio-only users. Either split `Bimwright.Rvt.Server` (stdio) from `Bimwright.Rvt.Server.Http` (SSE), or conditionally pull in AspNetCore only for the HTTP path.
- **Plugin ZIP size** — strip non-win-x64 entries from `runtimes/` in `scripts/stage-plugin-zip.ps1`. R25+ zips drop from ~16 MB → ~5 MB.

## v0.3 — ecosystem + async

- **Async job polling (A8)** — long-running Revit operations (full-model recompute, export-to-IFC, large family load) currently block the 30 s response timeout. Add a `jobs/status/<id>` pattern so the model can fire and check later.
- **Aggregator listings** — submit to Smithery, mcp.so, PulseMCP, MCP Market, Cline's registry, MseeP. Each has its own metadata format; roll changes through `server.json` first where possible.
- **Prompt library** — reintroduce the `MCP prompts` feature that was stripped from v0.1.0 (the original lived in `RevitPrompts.cs` before the fresh-repo split). Generic prompts only this time; no project-specific DB coupling.
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

## Security notes

Current v0.1.0 hardening covers launch-day concerns. Deferred:

- **S4 pagination** — tool responses can be large (100-item DTO arrays). No pagination contract yet; client is on its own for chunking.
- **Signed ToolBaker bakes** — baked tools persist to SQLite without signing. A malicious user with write access to the SQLite file could inject code. Acceptable for single-user dev; v1.0 territory for shared environments.
- **LAN bind warning** — `BIMWRIGHT_ALLOW_LAN_BIND=1` flips to `0.0.0.0` with only a stderr warning. Consider requiring a second env var or confirming on first run.

If you're running Bimwright in an environment where any of these matter, open an issue — it helps prioritize.
