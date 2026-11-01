# WPF Agent Reports window

Windows-only STA executable that opens the production `ReportsWindow` (both display modes) over the
production `ReportsViewModel`, with an in-memory runtime, a scripted navigation service and a fixed
owner / work area (`Fakes.cs`). The data in `DemoData.cs` is synthetic: `DEMO-A.rvt`, `DEMO-B.rvt` and
the token-only family `Demo-pump.rfa`. No Revit, no report store or folder watcher, no clipboard, and no
writes to the user's config: window-state saves go to an in-memory list.
Requires the .NET 10 SDK and an interactive Windows session; windows flash briefly.

```powershell
dotnet run --project tests/RvtMcp.Reports.Tests -c Release
dotnet run --project tests/RvtMcp.Reports.Tests -c Release -- --check 20
```

Revit 2022–2024 use .NET Framework. Run the same focus/table checks on that runtime too:

```powershell
dotnet restore tests/RvtMcp.Reports.Tests -p:TargetFramework=net48 -p:LangVersion=latest
dotnet run --project tests/RvtMcp.Reports.Tests -c Release --no-restore -p:TargetFramework=net48 -p:LangVersion=latest -- --check 20
dotnet run --project tests/RvtMcp.Reports.Tests -c Release --no-restore -p:TargetFramework=net48 -p:LangVersion=latest -- --check 21
```

Restore the default target again before returning to the .NET 10 run.

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
10. Delete is an "X" icon; the first click turns it into the red confirm text, as wide as the text, and the second deletes. In compact mode Copy and Delete sit at the top right of the open card. Copy sends plain text and shows Success.
11. Status line: four tones in the PLAN colours, the hint when empty.
12. Empty: no active document shows the empty text; All shows the list (both modes).
13. Language: `fr` then `ja` change title, filter and outcome text; selection, mode and filter stay.
14. Mouse wheel over the body scrolls the outer region (both modes).
15. Keyboard: Tab reaches the mode buttons, filter, list and actions; Up / Down move the wide selection; Enter toggles a compact card.
16. Performance (printed as `PERF`): a 32 KiB body and a 5,000-change report each open, and one "Show N more" press completes, in under 1,500 ms.
17. Lifecycle: hide / show three times, `CloseForShutdown` closes for real, no `PropertyChanged` or `L.Changed` handlers left after `Dispose` or after mode switches.
18. No WPF binding errors after all scenarios.
19. Text size controls and shortcuts.
20. Visible sheet links preserve scroll position; off-screen keyboard focus reveals the link in both modes.
21. Long sections defer table construction, table pages stay bounded, links keep element identity, and Copy retains hidden content.
22. Compact day separators, filtered counts, time-only card labels with timestamp tooltips, unknown dates, and non-interactive headings.
23. New reports keep the reading card, pixel offset, FlowDocument and keyboard focus; the notice jumps to the top without opening a card; arrivals at the top and watcher updates while hidden behave correctly.
24. Updating the selected report preserves the Compact body position and card focus, with an Updated label; Wide retains its body scroll position too.
25. A 500-report feed stays virtualized, preserves a deep open body across capped batches, counts each arrival once, and handles deletion of the reading anchor and clock refresh; list/reveal handlers detach on disposal.

26. Short idle caption, host details inline on mouse enter and short text again on leave (no title tooltip); stable height/body scroll/focus, live transport/stopped updates, Compact PID/transport prefix visibly fits at minimum width, long endpoint ellipsis and full UIA help, hide resets hover.
27. Copy success/failure feedback, timer reset and disposal on a mode switch.
28. Opacity effects and reduced motion without height changes.
29. Independent Wide/Compact reading bookmarks.
30. Lazy background History, exact reason/record ID/before-after, pagination and mode preservation.

## Preview and snapshots

Check 7 also verifies that both modes state the process scope while the changes list
is collapsed. Checks 20–21 continue to cover the existing hyperlink and large-table fixes.

```powershell
dotnet run --project tests/RvtMcp.Reports.Tests -c Release -- --demo
dotnet run --project tests/RvtMcp.Reports.Tests -c Release -- --snapshot <folder>
```

`--demo` opens the window over the sample data; closing it ends the run.
`--snapshot` writes `wide.png`, `wide-other-model.png`, `compact.png`, `compact-expanded.png` and `empty.png` at 150 % scale.
It also exports `compact-days-{100,125,150,200}.png` and `compact-new-reports.png`;
Title/transport states, lazy History pages in both modes, and original Reports/Settings
icons at 16/24/32/64 pixels are also exported. All data are synthetic;
these are render-scale checks, not a claim about native monitor DPI.
This harness is separate from the cross-platform xUnit suite and does not establish live Revit acceptance (DPI, several monitors, hover tooltips, snap).
