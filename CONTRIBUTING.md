# Contributing to Bimwright

Thanks for your interest. Bimwright is a solo-maintained project shipping its first public release; this guide is the short version. Open an issue before a large PR so we can agree on scope.

## Dev setup

### Prereqs

- Windows 10/11 (Revit is Windows-only).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — required for the server and R25/R26 plugins.
- [.NET 10 SDK (preview)](https://dotnet.microsoft.com/download/dotnet/10.0) — required for the R27 plugin. Skip if you're not building R27.
- Visual Studio 2022+ or JetBrains Rider (optional — `dotnet build` from CLI works).
- One or more Revit installations (2022–2027) for runtime testing.

The Revit API itself is pulled from [Nice3point.Revit.Api.*](https://www.nuget.org/packages?q=Nice3point.Revit.Api) NuGet packages, so you don't need the Autodesk SDK installed to compile.

### Clone + build

```bash
git clone https://github.com/bimwright/rvt-mcp.git
cd bimwright
dotnet build src/Bimwright.Rvt.sln -c Debug
```

**Close every running Revit before building.** Plugin DLLs auto-deploy to `%APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright\` as part of the build, and Revit holds a file lock on loaded add-ins.

Build output lands in `src/plugin-r<nn>/bin/Debug/<tfm>/` and `src/server/bin/Debug/net8.0/`. The deploy target copies plugin DLLs + the `.addin` manifest into the Revit addins folder so the next Revit launch picks them up.

### Run tests

```bash
dotnet test tests/Bimwright.Rvt.Tests/Bimwright.Rvt.Tests.csproj
```

Tests are pure .NET 8 xUnit, no Revit dependency — they cover `ErrorSanitizer`, `SchemaValidator`, `BimwrightConfig`, `ToolsetFilter`, and `BatchExecutor`. Anything that needs a live Revit document is tested manually.

### Package the plugin ZIPs

```powershell
pwsh scripts/stage-plugin-zip.ps1 -Config Release
```

Produces `build/plugin-zip/Bimwright.Rvt.Plugin.R{22..27}.zip`. CI does this for you on every push.

## Project layout

See [ARCHITECTURE.md](ARCHITECTURE.md) for the conceptual model. Quick reference:

| Path | What lives here |
|------|-----------------|
| `src/server/` | MCP server, tool registration, stdio/HttpSse entry points |
| `src/shared/Handlers/` | One file per MCP tool handler (28 total) |
| `src/shared/Infrastructure/` | `CommandDispatcher`, `McpEventHandler`, `SchemaValidator`, `BatchExecutor` |
| `src/shared/Transport/` | `ITransportServer`, TCP + Named Pipe implementations |
| `src/shared/Security/` | `AuthToken`, `ErrorSanitizer`, `SecretMasker` |
| `src/shared/ToolBaker/` | Roslyn-based self-evolution engine |
| `src/shared/Config/` | `BimwrightConfig` — 3-layer precedence |
| `src/plugin-r<nn>/` | Revit-year shell: `App.cs`, `RibbonSetup.cs`, csproj, `.addin` |
| `tests/Bimwright.Rvt.Tests/` | xUnit tests (pure .NET 8, no Revit API) |
| `scripts/` | `stage-plugin-zip.ps1`, `install.ps1` |
| `.github/workflows/` | CI matrix build |

## Adding a new MCP tool

1. Write the handler in `src/shared/Handlers/<Verb><Noun>Handler.cs` implementing `IRevitCommand`. Return DTOs — never serialize Revit API objects directly.
2. Register in `src/shared/Infrastructure/CommandDispatcher.cs` constructor: `Register(new Handlers.YourHandler());`.
3. Add an `[McpServerTool]` method in the appropriate toolset class under `src/server/` (e.g. `QueryTools.cs`, `CreateTools.cs`). Use the 4-part description template: what, when-to-use, params, example.
4. If the tool mutates model state, put it in `CreateTools` / `ModifyTools` / `DeleteTools` (off by default).
5. Cover any non-trivial logic with an xUnit test in `tests/Bimwright.Rvt.Tests/`. Extract pure functions where possible — `BatchExecutor.Run` is a good template.
6. Manual smoke test in at least one Revit year before PR.

## Coding style

- Match the surrounding code. Existing handlers are the authoritative reference.
- DTOs are anonymous objects or records. Lowercase JSON property names — already the default Newtonsoft.Json contract.
- Unit conversion: Revit internal feet → mm at the DTO boundary via `SpecTypeId`/`ForgeTypeId`.
- No `try/catch` around things that can't fail. No defensive null checks on internal invariants. Trust the call chain.
- Comments explain *why*, not *what*. Identifiers explain *what*.
- `#if REVIT2024_OR_GREATER` / `REVIT2027_OR_GREATER` are the only version-sniffing allowed. Prefer `RevitCompat` helpers for shared call sites.

## Commit + PR

- One logical change per commit. Commit messages start with the task ID if the work is part of a tracked checklist, otherwise a short scope prefix (e.g. `handlers:`, `transport:`, `ci:`).
- Open a PR against `master`. CI must be green (all 6 plugin matrix jobs + server pack + tests). `fail-fast: false` so you'll see every broken row at once.
- Include a short "Tested with" line: which Revit year(s) you smoke-tested.

## Reporting bugs

Open a GitHub issue with:

- Revit version + year.
- Bimwright server version (`bimwright --version`) and plugin version (check the `.addin` manifest).
- Reproduction steps — ideally the exact MCP tool call and params.
- Logs from `%LOCALAPPDATA%\Bimwright\` — but **check for paths you don't want to share** (the sanitizer masks absolute paths in errors sent to the model, but local log files are unredacted).

## Security

Do **not** open a public issue for anything that looks like a privilege-escalation, auth bypass, or RCE via `send_code_to_revit` / ToolBaker. Contact the maintainer privately first — see `SECURITY.md` if present, otherwise email the address on the commit history.

## Code of Conduct

Be kind. Assume good faith. If that doesn't cover your situation, we'll default to [Contributor Covenant v2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).
