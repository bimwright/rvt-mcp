# WPF Agent Reports window

Windows-only STA executable that opens the production `ReportsWindow` (both display modes) over the
production `ReportsViewModel`, with an in-memory runtime, a scripted navigation service and a fixed
owner / work area (`Fakes.cs`). The data in `DemoData.cs` is synthetic: `DEMO-A.rvt`, `DEMO-B.rvt` and
the token-only family `Demo-pump.rfa`. No Revit, no report store or folder watcher, no clipboard, and no
writes to the user's config: window-state saves go to an in-memory list.
Requires the .NET 10 SDK and an interactive Windows session; windows flash briefly.

```powershell
dotnet run --project tests/RvtMcp.Reports.Tests -c Release
```

Exit code 0 requires all checks to pass:

1. Wide layout: 790×540 centred on the owner, full list, first report selected, summary / outcome pill / self-reported label, actual changes open.
2. Mode switch: Compact gets its own bounds, keeps selection (card open), filter and status; Wide returns to its earlier bounds; one save per switch with the mode and both bounds.
3. Reopen: saved mode and bounds restored, off-screen and oversized bounds clamp into the work area, empty state uses the defaults (Compact at the owner's right edge).
4. Maximize: content inside the real monitor work area; a mode switch while maximized returns to Normal; the old mode keeps `RestoreBounds`.
5. Markdown: every node type of the mapping table in the shown `FlowDocument` (blocks, runs, line breaks, mono code, fence indentation, list start 3, huge-number prefix, table columns, one `Hyperlink` per valid link, invalid links as text); thousands of nested emphasis levels lay out with a capped span depth.
6. Links: a live click sends the right document and elements; a dimmed link stays enabled, names the model in its tooltip and sends nothing; changing the active document restyles the same `FlowDocument`; count badge and view / sheet tags.
7. Actual changes: badges, notes for no data / no changes / incomplete / truncated, struck-through deleted ids, document titles, compact chip collapsed by default, 200 items per group, then up to 1,000 more per "Show N more" press.
8. Reload: unchanged data keeps the `FlowDocument` and both scroll offsets; a new report at the top keeps the selection.
9. `ShowReport` for another model's report: filter goes to All, the report is selected and scrolled into view (compact card open).
10. Two-step delete with the danger style, copy sends plain text and shows Success.
11. Status line: four tones in the PLAN colours, the hint when empty.
12. Empty: no active document shows the empty text; All shows the list (both modes).
13. Language: `fr` then `ja` change title, filter and outcome text; selection, mode and filter stay.
14. Mouse wheel over the body scrolls the outer region (both modes).
15. Keyboard: Tab reaches the mode buttons, filter, list and actions; Up / Down move the wide selection; Enter toggles a compact card.
16. Performance (printed as `PERF`): a 32 KiB body and a 5,000-change report each open, and one "Show N more" press completes, in under 1,500 ms.
17. Lifecycle: hide / show three times, `CloseForShutdown` closes for real, no `PropertyChanged` or `L.Changed` handlers left after `Dispose` or after mode switches.
18. No WPF binding errors after all scenarios.

## Preview and snapshots

```powershell
dotnet run --project tests/RvtMcp.Reports.Tests -c Release -- --demo
dotnet run --project tests/RvtMcp.Reports.Tests -c Release -- --snapshot <folder>
```

`--demo` opens the window over the sample data; closing it ends the run.
`--snapshot` writes `wide.png`, `wide-other-model.png`, `compact.png`, `compact-expanded.png` and `empty.png` at 150 % scale.
This harness is separate from the cross-platform xUnit suite and does not establish live Revit acceptance (DPI, several monitors, hover tooltips, snap).
