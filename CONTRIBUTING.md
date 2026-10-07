# Contributing to rvt-mcp

Thanks for your interest. rvt-mcp is a solo-maintained bimwright gateway; this guide is the short version. Open an issue before a large PR so we can agree on scope.

## Dev setup

### Prereqs

- Windows 10/11 (Revit is Windows-only).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — required for the server and the Revit 2025/2026 plugins.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — required for the Revit 2027 plugin. Skip if you're not building it.
- Visual Studio 2022+ or JetBrains Rider (optional — `dotnet build` from CLI works).
- One or more Revit installations (2022–2027) for runtime testing.

The Revit API itself is pulled from [Nice3point.Revit.Api.*](https://www.nuget.org/packages?q=Nice3point.Revit.Api) NuGet packages, so you don't need the Autodesk SDK installed to compile.

### Clone + build

```bash
git clone https://github.com/bimwright/rvt-mcp.git
cd rvt-mcp
dotnet build src/RvtMcp.sln -c Debug
```

**Close every running Revit before building.** Plugin DLLs auto-deploy to `%APPDATA%\Autodesk\Revit\Addins\<year>\RvtMcp\` as part of the build, and Revit holds a file lock on loaded add-ins.

Build output lands in `src/plugin-r<nn>/bin/Debug/<tfm>/` and `src/server/bin/Debug/net8.0/`. The deploy target copies plugin DLLs + the `.addin` manifest into the Revit addins folder so the next Revit launch picks them up.

### Run tests

```bash
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj
```

Tests run under .NET 8 xUnit without a running Revit. Coverage includes configuration, tool contracts and permissions, response guards/spill, batch behavior, discovery, per-instance binding and fake-listener transport scenarios. These do not replace live Revit acceptance tests.

Installer/uninstaller and packaging-script fixtures use disposable folders, not a real installation. Run them under both Windows PowerShell 5.1 and PowerShell 7:

```powershell
powershell -NoProfile -File tests/installer/upgrade.Tests.ps1
powershell -NoProfile -File tests/installer/uninstall.Tests.ps1
powershell -NoProfile -File tests/installer/pack-nupkg.Tests.ps1
pwsh -NoProfile -File tests/installer/upgrade.Tests.ps1
pwsh -NoProfile -File tests/installer/uninstall.Tests.ps1
pwsh -NoProfile -File tests/installer/pack-nupkg.Tests.ps1
pwsh -NoProfile -File tests/check-public-tree.Tests.ps1
pwsh -NoProfile -File scripts/check-public-tree.ps1
python -B -m unittest discover -s tests/installer -p "test_*.py" -v
```

Start PS5.1 from a normal Windows PowerShell environment; do not inherit a PS7-specific `PSModulePath`.

### Package the plugin ZIPs

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

Produces `build/plugin-zip/RvtMcp.Plugin.R{22..27}.zip` from existing Release build output. For the complete client package, use:

```powershell
pwsh scripts/package-client-setup.ps1 -Config Release
python tests/installer/verify-package.py build/client-setup/RvtMcp.Setup-v<x.y.z>-win-x64.zip --output <report.json>
pwsh scripts/publish-nupkg.ps1
```

Replace the version/report placeholders with actual paths. Setup packaging builds all six add-ins with auto-deploy disabled and writes a ZIP checksum sidecar. The verifier checks the archive and runs its server in a disposable profile; it does not contact Revit unless `--live-2027` is explicitly passed. Use a fresh output directory for each verification run. NuGet packaging writes the `.nupkg` and its `.sha256`; it does not publish unless `-Push` is explicitly passed. Neither command performs a GitHub release. A dirty checkout requires `-AllowDirty` for a throwaway Setup, never a release artifact.

## Project layout

See [ARCHITECTURE.md](ARCHITECTURE.md) for the conceptual model. Quick reference:

| Path | What lives here |
|------|-----------------|
| `src/server/` | MCP server, tool registration, stdio/HttpSse entry points |
| `src/shared/Handlers/` | One file per Revit command handler; 46 tools on bare CLI, 233 with all toolsets, 236 with all + adaptive bake |
| `src/shared/Infrastructure/` | `CommandDispatcher`, `McpEventHandler`, `SchemaValidator`, `BatchExecutor` |
| `src/shared/Transport/` | `ITransportServer`, TCP + Named Pipe implementations |
| `src/shared/Security/` | `AuthToken`, `ErrorSanitizer`, `SecretMasker` |
| `src/shared/ToolBaker/` | Roslyn-based self-evolution engine |
| `src/shared/Config/` | `RvtMcpConfig` — 3-layer precedence |
| `src/plugin-r<nn>/` | Revit-year shell: `App.cs`, `RibbonSetup.cs`, csproj, `.addin` |
| `tests/RvtMcp.Tests/` | xUnit tests (pure .NET 8, no Revit API) |
| `scripts/` | Setup/NuGet packaging, public-tree checks, install and uninstall entrypoints |
| `.github/workflows/` | CI matrix build |

## Adding a new MCP tool

1. Write the handler in `src/shared/Handlers/<Verb><Noun>Handler.cs` implementing `IRevitCommand`. Return DTOs — never serialize Revit API objects directly.
2. Register in `src/shared/Infrastructure/CommandDispatcher.cs` constructor: `Register(new Handlers.YourHandler());`.
3. Add an `[McpServerTool]` method in the appropriate toolset class under `src/server/` (e.g. `QueryTools` or `CreateTools` in `Program.cs`). Use the 4-part description template: what, when-to-use, params, example.
4. Choose the domain toolset and explicitly set all four permission hints for the strongest optional branch. Model/file writes must not be marked read-only; destructive descriptions explain recovery or lack of Undo. Arbitrary send_code intentionally has no annotations. The bare CLI defaults to `query,create,view,meta`; fresh installs seed `all`.
5. Cover any non-trivial logic with an xUnit test in `tests/RvtMcp.Tests/`. Extract pure functions where possible — `BatchExecutor.Run` is a good template.
6. Manual smoke test in at least one Revit year before PR.

## Coding style

- Match the surrounding code. Existing handlers are the authoritative reference.
- DTOs are anonymous objects or records. Lowercase JSON property names — already the default Newtonsoft.Json contract.
- Unit conversion: Revit internal feet → mm at the DTO boundary via `SpecTypeId`/`ForgeTypeId`.
- No `try/catch` around things that can't fail. No defensive null checks on internal invariants. Trust the call chain.
- Comments explain *why*, not *what*. Identifiers explain *what*.
- `#if REVIT2024_OR_GREATER` / `REVIT2027_OR_GREATER` are the only version-sniffing allowed. Prefer `RevitCompat` helpers for shared call sites.

## Documentation privacy

This repository is public. Keep maintainer research, development plans, session handoffs, draft issue replies, and raw live-test evidence in a separate private repository, not in product documentation. Review logs and screenshots for local paths, model data, and identifiers before sharing them.

Public documentation should describe shipped behavior and reproducible contributor workflows. Tests must use self-contained, non-sensitive fixtures committed here, never depend on private notes or a sibling checkout. Do not force-add ignored internal-document paths.

## Commit + PR

- One logical change per commit. Commit messages start with the task ID if the work is part of a tracked checklist, otherwise a short scope prefix (e.g. `handlers:`, `transport:`, `ci:`).
- Open a PR against `master`. CI must be green (all 6 plugin matrix jobs + server pack + tests). `fail-fast: false` so you'll see every broken row at once.
- Include a short "Tested with" line: which Revit year(s) you smoke-tested.

## Reporting bugs

Open a GitHub issue with:

- Revit version + year.
- rvt-mcp server version (MCP `initialize` → `serverInfo.version`) and plugin DLL file/assembly version; identify the selected release. The command is `rvt-mcp`, not `bimwright`.
- Reproduction steps — ideally the exact MCP tool call and params.
- Logs from `%LOCALAPPDATA%\Bimwright\rvt-mcp\` — but **check for paths you don't want to share** (the sanitizer masks absolute paths in errors sent to the model, but local log files are unredacted).

## Testing & drift detection

### Schema drift
The canonical tool surface is snapshot to `tests/RvtMcp.Tests/Golden/tools-list.json`. CI compares each build against this file. A diff fails the build.

When you intentionally add, rename, or reshape a tool:
1. Run `UPDATE_SNAPSHOTS=1 dotnet test` locally.
2. Review the diff in `Golden/tools-list.json`.
3. Commit the updated golden file alongside your source change in the same PR.

### Weak-model benchmark
Run the Haiku benchmark before merging when your PR:
- Adds five or more new handler files, OR
- Edits any tool's description text, OR
- Precedes tagging a minor or major release.

The benchmark uses Claude Code to spawn a Haiku sub-agent; no Anthropic API key is required. The procedure is maintainer-internal.

A ≥15% param-accuracy drop vs the last baseline blocks merge until the regression is understood. Smaller drops are human-review.

### Client compatibility
Primary verified clients are documented in `README.md` and `AGENTS.md`. Client wiring is not fully automated in tests; if you hit a client-specific bug, open an issue with a minimal repro so we can add regression coverage once the behavior is understood.

### Naming convention
Tool names and parameter keys use `snake_case`. We do not maintain aliases for tool names from other Revit-MCP implementations. If you are migrating from another implementation and a specific name mismatch is blocking you, open an issue — we will consider aliases when three or more users request the same one.

## Security

Do **not** open a public issue for anything that looks like a privilege-escalation, auth bypass, or RCE via `send_code_to_revit` / ToolBaker. Contact the maintainer privately first — see `SECURITY.md` if present, otherwise email the address on the commit history.

## Code of Conduct

Be kind. Assume good faith. If that doesn't cover your situation, we'll default to [Contributor Covenant v2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).
