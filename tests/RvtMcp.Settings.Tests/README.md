# WPF Settings regression

Windows-only STA executable that opens the production `SettingsWindow` against in-memory
presentations (`FakePresentation.cs`): no Revit, no MCP connection, no config file writes.
Requires the .NET 10 SDK and an interactive Windows session; windows flash briefly.

```powershell
dotnet run --project tests/RvtMcp.Settings.Tests -c Release
```

Exit code 0 requires all checks to pass:

- Four tabs in order General / Toast / Tools / About; `Tools` stays invariant across a language change, which also keeps the selected tab.
- Apply and Discard changes are enabled only with staged changes, the footer says "Unsaved changes", Discard reverts both the value and the control, Apply confirms.
- A failed Apply key shows its error under the matching row and stays staged; a retry clears it. Immediate-save warnings render under their row, or in the footer when no row matches.
- The toast switch applies immediately (not staged) and disables, but keeps, the idle duration.
- The connection pill is green when a client is connected, amber while waiting, neutral when stopped; Copy is disabled with an explanation when there is no TCP port.
- The State row's switch stops/starts the listener and its restart button brings it back on a new port; both are reflected in the transport line, Copy and the restart button.
- The Tools grid has five read-only columns; switching to Tools refreshes the catalog, a combo selection inside another tab does not.
- Opening and closing the window three times leaves no `PropertyChanged` / `LanguageChanged` handlers and disposes both presentations.

## Preview and snapshots

```powershell
dotnet run --project tests/RvtMcp.Settings.Tests -c Release -- --demo
dotnet run --project tests/RvtMcp.Settings.Tests -c Release -- --snapshot <folder>
```

`--demo` opens the window with sample data; Apply / Discard work in memory only.
`--snapshot` renders each tab to `<folder>/<tab>.png`, plus `license.png`, at 150 % scale (the Toast tab with one staged change).
This harness is separate from the cross-platform xUnit suite and does not establish live Revit acceptance.
