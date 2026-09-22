# Toast positioning crash — 2026-09-22

During issue #13's final typed MCP acceptance on Revit 2027 (`27.0.10.13`), Revit exited after the expected missing-host error. The door success case and cleanup could not finish. A disposable scratch project had been activated for this test; the original HVAC sample was unmodified.

The managed crash dump identifies `System.Windows.Media.Animation.AnimationException` on `McpToastWindow.Top`, with inner exception `DoubleAnimation cannot use default origin value of NaN`. The stack passes through `Window.Show` and `McpToastManager.Complete`. This establishes a toast failure, not a failure of the hosted-family overload. Raw dumps remain local and are not committed.

`Complete` inserted an unpositioned window into the active list before `Show`. WPF windows default to `NaN` coordinates, and a reentrant close/reflow could start a destination-only animation before initial placement. The fix positions the stack before `Show`; animation handles an unset origin by direct placement, uses explicit finite origins otherwise, and direct reflow removes previous animation clocks.

The [real WPF regression executable](../../tests/RvtMcp.Toast.Tests/README.md) reproduces the same fatal exception before the fix and passes both cases after it. The existing xUnit suite passes 549 tests. Release builds and deployment for Revit 2022/2024/2027 complete with zero warnings/errors; live revalidation is recorded separately with [issue #13](issue-13/HANDOFF.md).

Local diagnostic provenance: dump SHA-256 `15F44E77EC287DA058D74F3D609772BF16CD42161DC819D6FA80C7BF43E1AC1F`. Full diagnostic output is ignored under `artifacts/issue-13/managed-crash.txt`; before/after regression logs are `toast-red.log` and `toast-green.log` in that directory. The dump parser printed the exception before encountering an unrelated error while enumerating another thread; the independent WPF reproduction confirms the diagnosis.
