# WPF Settings and History regression

Windows-only STA executable that opens the production `SettingsWindow`, `HistoryWindow`
and `FailureIssuesView` against in-memory presentations and host/ledger facades
(`FakePresentation.cs`, `HistoryTestRuntime.cs`). Warning-policy/journal checks also link the
production `SettingsViewModel` and configuration writer, using disposable temporary config files
and restoring process-local overrides. No Revit, MCP connection, user config edits or real ledger writes.
Requires the .NET 10 SDK and an interactive Windows session; windows flash briefly.

```powershell
dotnet run --project tests/RvtMcp.Settings.Tests -c Release
```

Exit code 0 requires all checks to pass:

- History decision buttons allow reviewed inactive/closed-model issues, but still reject running operations, incomplete acceptance and missing native permission constraints. A Rework click records the selected issue's scope/revision, not an unrelated active model.
- Actual code/SQL TextBoxes preserve source characters when WPF Copy generates its payload, including long code rendered inside an Expander. The test cancels clipboard delivery so the user's clipboard stays untouched.
- Model-kind labels and their rendered templates follow English/French/Japanese language changes without changing the selected review.
- Activities title, shared italic search/reason placeholder style, compact accessible Copy/Expand buttons, expansion and fixed model/search widths (including long names).
- Real warning-policy Apply/Discard, reopen, failed save/retry and host environment override; journal presets exactly 1h/4h/8h/12h/24h/2d/7d/14d, compact rendered labels and actual saved 14d expiry; no long dashes in visible General settings.

- Four tabs in order General / Toast / Tools / About; `Tools` stays invariant across a language change, which also keeps the selected tab.
- Apply and Discard changes are enabled only with staged changes, the footer says "Unsaved changes", Discard reverts both the value and the control, Apply confirms.
- A failed Apply key shows its error under the matching row and stays staged; a retry clears it. Immediate-save warnings render under their row, or in the footer when no row matches.
- The toast switch applies immediately (not staged) and disables, but keeps, the idle duration.
- The connection pill is green when a client is connected, amber while waiting, neutral when stopped; Copy is disabled with an explanation when there is no TCP port.
- The State row's switch stops/starts the listener and its restart button brings it back on a new port; both are reflected in the transport line, Copy and the restart button.
- The Tools grid has five read-only columns; switching to Tools refreshes the catalog, a combo selection inside another tab does not.
- Opening and closing the window three times leaves no `PropertyChanged` / `LanguageChanged` handlers and disposes both presentations.

Run only the History regressions, model-picker interactions or real Settings adapter checks:

```powershell
dotnet run --project tests/RvtMcp.Settings.Tests -c Release -- --history-only
dotnet run --project tests/RvtMcp.Settings.Tests -c Release -- --model-picker-only
dotnet run --project tests/RvtMcp.Settings.Tests -c Release -- --warning-policy-only
```

The history checks link the production view source, not a copy of its enablement or text-rendering logic. The in-memory facades do not verify native Revit dispatch, real ledger persistence, session-log privacy or grant execution; those require their separate tests.

## Preview and snapshots

```powershell
dotnet run --project tests/RvtMcp.Settings.Tests -c Release -- --demo
dotnet run --project tests/RvtMcp.Settings.Tests -c Release -- --snapshot <folder>
```

`--demo` opens the window with sample data; Apply / Discard work in memory only.
`--snapshot` renders each tab to `<folder>/<tab>.png`, plus `license.png`, at 150 % scale (the Toast tab with one staged change).
This harness is separate from the cross-platform xUnit suite and does not establish live Revit acceptance.
