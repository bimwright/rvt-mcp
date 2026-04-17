# Bimwright.Rvt.Tests

xUnit test project. Covers unit-test scope + schema/tool-surface drift snapshot.

## Layout

- `*Tests.cs` at the top level — xUnit fact/theory files.
- `Helpers/` — test-only utilities (e.g. `SnapshotSerializer`).
- `Golden/` — committed snapshot files. See below.

## Running

```bash
dotnet test tests/Bimwright.Rvt.Tests/Bimwright.Rvt.Tests.csproj
```

All tests live in one project; there is no matrix.

If the build fails copying `Bimwright.Rvt.Server.exe` because a running MCP session holds it open, add `-c Release` to use a separate output directory, or `/mcp` disconnect in Claude Code first.

## Golden snapshot — `Golden/tools-list.json`

This file is the canonical capture of every MCP tool exposed by the server: tool name, description hash (SHA-256 — changes when description text changes, but diffs stay small), and input schema (parameter names, types, required flags).

`ToolsListSnapshotTests` captures the current tool surface via reflection on `[McpServerToolType]`-annotated classes in `Bimwright.Rvt.Server`, serializes it with stable ordering, and compares against `Golden/tools-list.json`.

### On mismatch

Test output shows the diff. Two possibilities:

- **Accidental drift** — you renamed a tool or parameter without meaning to. Fix the source; the test will pass again.
- **Intentional change** — you added / renamed / reshaped a tool on purpose. Update the golden file (next section) and commit it with your change.

### Updating the golden file

```powershell
# PowerShell
$env:UPDATE_SNAPSHOTS="1"; dotnet test tests/Bimwright.Rvt.Tests/Bimwright.Rvt.Tests.csproj --filter "ToolsListSnapshotTests"; Remove-Item Env:UPDATE_SNAPSHOTS
```

```bash
# bash / zsh
UPDATE_SNAPSHOTS=1 dotnet test tests/Bimwright.Rvt.Tests/Bimwright.Rvt.Tests.csproj --filter "ToolsListSnapshotTests"
```

Then commit the updated `Golden/tools-list.json` alongside the source change in the same PR. Reviewers read the JSON diff.

### First-run bootstrap

If `Golden/tools-list.json` does not exist, the test auto-creates it on the first run and passes with a warning printed to stderr. Commit the generated file.
