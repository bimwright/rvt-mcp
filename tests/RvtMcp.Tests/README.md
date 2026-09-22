# RvtMcp.Tests

xUnit test project. Covers unit-test scope + schema/tool-surface drift snapshot.

## Layout

- `*Tests.cs` at the top level — xUnit fact/theory files.
- `Helpers/` — test-only utilities (e.g. `SnapshotSerializer`).
- `Golden/` — committed snapshot files. See below.

## Running

```bash
dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj -c Release
```

The cross-platform xUnit tests live in this project. The server reference builds without an app host into `tests/server-staging/`, avoiding the executable held by running MCP sessions.

`PointBasedPlacementTests` compiles the production handler against API doubles to exercise placement-type selection, host/coordinate validation, overload choice and rollback. These tests do not replace live Revit acceptance; see [issue #13 evidence](../../docs/testing/issue-13/HANDOFF.md).

`MepConnectionHandlerTests` executes the complete production connection handler with API doubles, covering direct and fitting-mediated connections, repeat calls, false connectivity and rollback after partial API mutation. The [Revit 2027 review follow-up](../../docs/testing/issue-12/REVIEW-FOLLOWUP.md) separately verifies real fitting connectors and public typed-tool arguments.

Real WPF positioning/animation tests run separately on Windows via [RvtMcp.Toast.Tests](../RvtMcp.Toast.Tests/README.md).

## Golden snapshot — `Golden/tools-list.json`

This file is the canonical capture of every MCP tool exposed by the server: tool name, description hash (SHA-256 — changes when description text changes, but diffs stay small), and input schema (parameter names, types, required flags).

`ToolsListSnapshotTests` captures the current tool surface via reflection on `[McpServerToolType]`-annotated classes in `RvtMcp.Server`, serializes it with stable ordering, and compares against `Golden/tools-list.json`.

### On mismatch

Test output shows the diff. Two possibilities:

- **Accidental drift** — you renamed a tool or parameter without meaning to. Fix the source; the test will pass again.
- **Intentional change** — you added / renamed / reshaped a tool on purpose. Update the golden file (next section) and commit it with your change.

### Updating the golden file

```powershell
# PowerShell
$env:UPDATE_SNAPSHOTS="1"; dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj --filter "ToolsListSnapshotTests"; Remove-Item Env:UPDATE_SNAPSHOTS
```

```bash
# bash / zsh
UPDATE_SNAPSHOTS=1 dotnet test tests/RvtMcp.Tests/RvtMcp.Tests.csproj --filter "ToolsListSnapshotTests"
```

Then commit the updated `Golden/tools-list.json` alongside the source change in the same PR. Reviewers read the JSON diff.

### First-run bootstrap

If `Golden/tools-list.json` does not exist, the test auto-creates it on the first run and passes with a warning printed to stderr. Commit the generated file.
